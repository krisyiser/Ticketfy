using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed partial class GiftcardService : IGiftcardService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public GiftcardService(AppDbContext db, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public GiftcardService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }
    public async Task<AccountStatementPageDto> GetStatementPageAsync(string cardNumber, int page = 0, int size = 100, LedgerReadCut? cut = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.WalletManage, false);
        return await CommercialLedgerReader.AccountAsync(db, AccountKind.Giftcard, cardNumber, page, size, cut);
    }
    public async Task<List<GiftcardEntity>> GetAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.WalletManage, false);
        var rows = await db.Giftcards.AsNoTracking().Where(g => g.IsActive).OrderBy(g => g.CardNumber).ThenBy(g => g.Id)
            .Take(LegacyCatalogReadPolicy.Limit + 1).ToListAsync();
        LegacyCatalogReadPolicy.RequireComplete(rows.Count > LegacyCatalogReadPolicy.Limit);
        return rows;
    }
    public async Task<GiftcardEntity?> GetByCardNumberAsync(string cardNumber)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.PosCheckout, false);
        return await db.Giftcards.AsNoTracking().SingleOrDefaultAsync(g => g.CardNumber == cardNumber && g.IsActive);
    }
    public Task<bool> DeductBalanceAsync(string cardNumber, decimal amount) =>
        throw new InvalidOperationException("El monedero se descuenta únicamente dentro de una venta transaccional.");
    public Task<bool> RedeemBalanceAsync(string cardNumber, decimal amountToRedeem, string saleId = "") =>
        throw new InvalidOperationException("Utilice el pago de monedero en el cobro de la venta.");
    public async Task<(bool IsValid, decimal Balance, string Error)> ValidateCardAsync(string cardNumber)
    {
        var card = await GetByCardNumberAsync(cardNumber);
        return card is null ? (false, 0m, "Monedero inexistente o inactivo.") : (true, card.Balance, "");
    }
    public async Task CreateCardAsync(string cardNumber, decimal initialBalance, string? customerId = null)
    {
        if (string.IsNullOrWhiteSpace(cardNumber) || initialBalance < 0 || CommercialLedger.Money(initialBalance) != initialBalance)
            throw new InvalidOperationException("Número o saldo inicial inválido.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.WalletManage);
        var existing = await db.Giftcards.SingleOrDefaultAsync(g => g.CardNumber == cardNumber);
        if (existing is not null) throw new InvalidOperationException("El monedero ya existe; use una recarga.");
        var card = new GiftcardEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            CardNumber = cardNumber.Trim(),
            Balance = initialBalance,
            CustomerId = customerId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        db.Giftcards.Add(card);
        string op = "WALLET_CREATE:" + card.Id;
        if (initialBalance > 0)
        {
            CommercialLedger.Account(db, actor, op, AccountKind.Giftcard, card.CardNumber, card.Id, initialBalance, "CASH_LOAD");
            await CommercialLedger.CashAsync(db, actor, op, card.Id, initialBalance, false, MovementType.RecargaMonedero, "Emisión y carga de monedero en efectivo");
        }
        CommercialLedger.Journal(db, actor, op, "WALLET_CREATED", card.Id, initialBalance, "Emisión");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public Task RechargeAsync(string cardId, decimal amount, PaymentMethod method, string activeShiftId) =>
        throw new InvalidOperationException("La recarga requiere una clave de operación persistente.");
    public async Task RechargeAsync(string cardId, decimal amount, PaymentMethod method, string activeShiftId, string operationId)
    {
        CommercialLedger.OperationId(operationId);
        if (amount <= 0 || CommercialLedger.Money(amount) != amount || method is not (PaymentMethod.Efectivo or PaymentMethod.Transferencia))
            throw new InvalidOperationException("La recarga requiere importe positivo y efectivo o transferencia confirmada.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.WalletManage);
        if (actor.ShiftId != activeShiftId) throw new InvalidOperationException("La recarga debe pertenecer al turno actual.");
        await PostRechargeAsync(db, actor, cardId, amount, method, operationId);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    // Caller owns authorization and the transaction, including recovery approval.
    internal static async Task PostRechargeAsync(AppDbContext db, OperationIdentity actor, string cardId,
        decimal amount, PaymentMethod method, string operationId)
    {
        await CommandIdentity.BindAsync(db, actor, operationId, "WALLET_LOAD", cardId, amount, method);
        var prior = await db.TransactionJournal.SingleOrDefaultAsync(e => e.OperationId == operationId && e.EventType == "WALLET_LOADED");
        if (prior is not null)
        {
            if (prior.EntityId != cardId || prior.Amount != amount) throw new InvalidOperationException("Clave de recarga reutilizada.");
            return;
        }
        var card = await db.Giftcards.SingleAsync(g => g.Id == cardId && g.IsActive);
        var proof = method == PaymentMethod.Transferencia
            ? await IncomingTransferService.RequireProofAsync(db, actor, operationId, amount, IncomingTransferService.WalletPurpose, cardId) : null;
        card.Balance += amount;
        CommercialLedger.Account(db, actor, operationId, AccountKind.Giftcard, card.CardNumber, card.Id, amount, proof is null ? "CASH_LOAD" : "TRANSFER_LOAD");
        if (proof is null) await CommercialLedger.CashAsync(db, actor, operationId, card.Id, amount, false, MovementType.RecargaMonedero, "Recarga de monedero");
        else db.IncomingTransferReceipts.Add(IncomingTransferService.Receipt(actor, proof, card.Id));
        CommercialLedger.Journal(db, actor, operationId, "WALLET_LOADED", card.Id, amount, method.ToString());
    }
}
