using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Models;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Operations;
using System.Text.Json;

namespace Ticketfy.Services.Implementations;

public partial class SaleService : ISaleService
{
    public event Action<SaleDto>? SaleSaved;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IAuditService _auditService;
    private readonly CommercialOperationContext? _operations;
    private readonly TimeProvider _clock;
    private readonly CoOccurrenceQueue _coOccurrenceQueue;

    public SaleService(IDbContextFactory<AppDbContext> contextFactory, CoOccurrenceQueue coOccurrenceQueue,
        IAuditService auditService, CommercialOperationContext? operations = null, TimeProvider? clock = null)
    {
        _contextFactory = contextFactory;
        _auditService = auditService;
        _coOccurrenceQueue = coOccurrenceQueue;
        _operations = operations;
        _clock = clock ?? TimeProvider.System;
    }

    private Task<OperationIdentity> AuthorizeAsync(AppDbContext db, string permission) =>
        (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(db, permission);

    public Task<SaleDto> SaveAsync(SaleDto sale, FiscalOutboxRequest? fiscalOutbox = null) => SaveCoreAsync(sale, fiscalOutbox);

    private async Task<SaleDto> SaveCoreAsync(SaleDto sale, FiscalOutboxRequest? fiscalOutbox = null, bool recovering = false)
    {
        CommercialDocumentLimits.ValidateSale(sale);
        string operationId = CommercialLedger.OperationId(sale.OperationId ?? sale.Id);
        if (sale.Items is null || sale.Items.Count == 0 || sale.Total < 0 || CommercialLedger.Money(sale.Total) != sale.Total)
            throw new InvalidOperationException("La venta requiere partidas e importe válido.");
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        OperationIdentity actor;
        if (recovering)
        {
            var pending = await CheckoutDocumentQuery.Bounded(db).AsNoTracking().SingleAsync(o => o.OperationId == operationId);
            var shift = await db.Shifts.SingleOrDefaultAsync(s => s.Id == sale.ShiftId && s.RegisterId == sale.RegisterId && s.CashierUserId == sale.CashierUserId && s.IsOpen == 1);
            if (pending.State != CheckoutOperationState.PaymentAuthorized || shift is null || string.IsNullOrEmpty(sale.CashierUserId))
                throw new InvalidOperationException("El cobro requiere conciliación en su caja y turno originales.");
            var recoveredUser = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == Guid.Parse(sale.CashierUserId));
            bool supervised = await db.TransactionJournal.AsNoTracking().AnyAsync(j => j.OperationId == "RESOLVE:" + operationId
                && j.EventType == "CHECKOUT_FUNDS_VERIFIED" && j.EntityId == operationId && j.Amount == sale.Total
                && j.RegisterId == sale.RegisterId && j.ShiftId == sale.ShiftId && j.CashierUserId != null);
            var recoveryPermissions = new Ticketfy.Core.Services.UserRolePermissionEngine(new SettingsService(_contextFactory));
            if (recoveredUser is null || (!supervised && (!recoveredUser.IsActive || !await recoveryPermissions.HasPermissionAsync(
                    recoveryPermissions.MapToModel(recoveredUser).DisplayRole, PermissionCatalog.PosCheckout))))
                throw new UnauthorizedAccessException("El operador original ya no está autorizado; requiere conciliación supervisada.");
            actor = new OperationIdentity(sale.CashierUserId, sale.RegisterId!, shift.Id);
        }
        else actor = await AuthorizeAsync(db, PermissionCatalog.PosCheckout);
        var intent = await CheckoutDocumentQuery.Bounded(db).AsNoTracking().SingleOrDefaultAsync(o => o.OperationId == operationId);
        string? recordedComposition = null;
        if (intent is not null)
        {
            var original = CommercialDocumentLimits.ReadSale(intent.SalePayload);
            if (CommandIdentity.Sale(original) != CommandIdentity.Sale(sale)
                || intent.CashierUserId != actor.UserId || intent.ShiftId != actor.ShiftId || intent.RegisterId != actor.RegisterId)
                throw new InvalidOperationException("El intento de cobro pertenece a otros datos, usuario o turno.");
            recordedComposition = original.CheckoutInventoryFingerprint;
        }
        if (await db.IncomingTransferIntents.AnyAsync(i => i.OperationId == operationId))
            throw new InvalidOperationException("La clave pertenece a un abono o recarga, no a una venta.");
        await CommandIdentity.BindAsync(db, actor, operationId, "SALE", CommandIdentity.Sale(sale));
        var existing = await SaleDocumentReader.HeaderAsync(db, operationId, operation: true);
        if (existing is not null)
        {
            if (existing.CashierUserId != actor.UserId || existing.Total != sale.Total)
                throw new InvalidOperationException("La clave de operación ya fue utilizada para otro cobro.");
            return await MapWithLedgersAsync(db, existing);
        }
        if (intent?.State == CheckoutOperationState.Failed) throw new InvalidOperationException(CheckoutStatePolicy.ClosedIntent);
        if (intent?.State == CheckoutOperationState.RequiresReconciliation || intent is not null && !Enum.IsDefined(intent.State))
            throw new InvalidOperationException(CheckoutStatePolicy.ReconciliationRequired);
        if (!recovering) await CheckoutStatePolicy.RequireNoOtherPendingAsync(db, actor.ShiftId, operationId);
        var (snapshots, payments, inventoryGraph) = await PrepareSaleAsync(db, sale, recovering);
        if (intent is not null && recordedComposition != inventoryGraph.CheckoutFingerprint(sale.Items.Select(i => i.ProductId)))
            throw new InvalidOperationException(recordedComposition is null
                ? MissingCheckoutCompositionMessage : ChangedCheckoutCompositionMessage);
        string saleId = await GenerateNextSaleFolioAsync(db);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(actor.UserId));
        var entity = new SaleEntity
        {
            Id = saleId,
            OperationId = operationId,
            Date = _clock.GetUtcNow().ToString("O"),
            Total = sale.Total,
            PaidAmount = sale.PaidAmount,
            ChangeAmount = sale.ChangeAmount,
            PaymentMethod = sale.PaymentMethod,
            CustomerId = sale.CustomerId,
            IsCredit = payments.Any(p => p.Kind == TenderKind.Credit) ? 1 : 0,
            CashAmount = payments.Where(p => p.Kind == TenderKind.Cash).Sum(p => p.Amount),
            CardAmount = payments.Where(p => p.Kind == TenderKind.Card).Sum(p => p.Amount),
            GiftcardAmount = payments.Where(p => p.Kind == TenderKind.Giftcard).Sum(p => p.Amount),
            GiftcardNumber = sale.GiftcardNumber,
            ShiftId = actor.ShiftId,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId,
            CashierName = user.FullName,
            SerieFolio = saleId,
            Status = SaleStatus.Completed,
            LedgerVersion = 1,
            CommittedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        db.Sales.Add(entity);
        if (snapshots.Select(i => i.ProductId).Distinct().Take(2).Count() == 2)
            db.RecommendationJobs.Add(new RecommendationJobEntity { SaleId = saleId });
        for (int i = 0; i < snapshots.Count; i++)
        {
            var item = snapshots[i];
            var line = new SaleLineEntity
            {
                SaleId = saleId,
                LineNumber = i + 1,
                ProductId = item.ProductId,
                Name = item.Name,
                Quantity = (decimal)item.Quantity,
                UnitPrice = item.OriginalUnitPrice,
                Total = item.TotalPrice,
                Discount = item.AppliedDiscountAmount + item.ProratedGlobalDiscountAmount,
                Unit = item.Unit,
                PromotionId = item.AppliedPromotionId
            };
            decimal cost = await DeductInventoryAsync(db, inventoryGraph, item.ProductId, (decimal)item.Quantity, actor, operationId, saleId, line.Id);
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == item.ProductId);
            line.UnitCost = cost;
            line.CostTotal = CommercialLedger.Money(cost * line.Quantity);
            line.Tax = CommercialLedger.Money(line.Total - line.Total / (1 + product.TaxRate));
            db.SaleLines.Add(line);
            snapshots[i] = item with { Cost = cost, TaxAmount = line.Tax, SaleLineId = line.Id };
        }
        entity.TotalCost = db.SaleLines.Local.Where(l => l.SaleId == saleId).Sum(l => l.CostTotal);
        entity.Profit = entity.Total - entity.TotalCost;
        entity.ItemsJson = CommercialDocumentLimits.RequirePayload(JsonSerializer.Serialize(snapshots, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.ListSaleItemSnapshotDto));
        foreach (var payment in payments) await PostPaymentAsync(db, actor, entity, payment);
        if (sale.CustomerId is not null && !payments.Any(p => p.Kind == TenderKind.Loyalty))
        {
            decimal earned = CommercialLedger.Money(snapshots.Sum(i => (decimal)i.PointsRewarded * (decimal)i.Quantity));
            if (earned > 0)
            {
                double points = (double)earned;
                if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET puntos_saldo = puntos_saldo + {points} WHERE id = {sale.CustomerId}") != 1)
                    throw new InvalidOperationException("Cliente inexistente.");
                CommercialLedger.Account(db, actor, operationId, AccountKind.Loyalty, sale.CustomerId, saleId, earned, "SALE_REWARD");
            }
        }
        CommercialLedger.Journal(db, actor, operationId, "SALE_COMPLETED", saleId, entity.Total, sale.PaymentMethod);
        var checkout = await CheckoutDocumentQuery.Bounded(db).SingleOrDefaultAsync(o => o.OperationId == operationId);
        if (checkout?.FiscalPayload is not null && fiscalOutbox is null)
            fiscalOutbox = new FiscalOutboxRequest(JsonSerializer.Deserialize(checkout.FiscalPayload,
                FacturamaJsonContext.Default.FacturamaCfdiRequest)!, checkout.FiscalIdempotencyKey!);
        if (checkout is not null)
        {
            checkout.SaleId = saleId; checkout.State = CheckoutOperationState.Completed;
            checkout.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"); checkout.FailureReason = null;
        }
        if (fiscalOutbox is not null)
        {
            if (string.IsNullOrWhiteSpace(fiscalOutbox.IdempotencyKey) || fiscalOutbox.IdempotencyKey.Length > 100)
                throw new InvalidOperationException("Clave fiscal inválida.");
            fiscalOutbox.Request.Folio ??= new string(saleId.Where(char.IsLetterOrDigit).ToArray());
            fiscalOutbox.Request.OrderNumber ??= fiscalOutbox.IdempotencyKey;
            db.FiscalOperations.Add(new FiscalOperationEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                SaleId = saleId,
                IdempotencyKey = fiscalOutbox.IdempotencyKey,
                OperationType = FiscalOperationType.Issue,
                PayloadJson = JsonSerializer.Serialize(fiscalOutbox.Request, FacturamaJsonContext.Default.FacturamaCfdiRequest),
                Status = IntegrationOperationStatus.Pending,
                CreatedAtUtc = entity.Date,
                UpdatedAtUtc = entity.Date,
                NextAttemptAtUtc = entity.Date
            });
            entity.EstadoFiscal = "EN COLA";
        }
        await db.SaveChangesAsync();
        var result = await MapWithLedgersAsync(db, entity);
        await transaction.CommitAsync();
        _coOccurrenceQueue.Signal();
        try { SaleSaved?.Invoke(result); } catch (Exception ex) { Serilog.Log.Error(ex, "Sale notification failed after commit {SaleId}", saleId); }
        return result;
    }


    private async Task<(List<SaleItemSnapshotDto> Items, List<SaleTenderDto> Payments, KitInventoryGraph Graph)> PrepareSaleAsync(AppDbContext db, SaleDto sale, bool recovering = false)
    {
        CommercialDocumentLimits.ValidateSale(sale);
        if (sale.Items is null || sale.Items.Count == 0 || sale.Total < 0 || CommercialLedger.Money(sale.Total) != sale.Total)
            throw new InvalidOperationException("La venta requiere partidas e importe válido.");
        if (sale.CustomerId is not null && !await db.Customers.AnyAsync(c => c.Id == sale.CustomerId && c.IsActive))
            throw new InvalidOperationException("Seleccione un cliente activo.");
        var payments = BuildPayments(sale);
        if (payments.Any(p => !Enum.IsDefined(p.Kind))) throw new InvalidOperationException("Medio de pago desconocido.");
        decimal cashPaid = payments.Where(p => p.Kind == TenderKind.Cash).Sum(p => p.Amount);
        if (sale.PaidAmount < 0 || sale.ChangeAmount < 0 || sale.ChangeAmount > sale.PaidAmount
            || CommercialLedger.Money(sale.PaidAmount) != sale.PaidAmount || CommercialLedger.Money(sale.ChangeAmount) != sale.ChangeAmount
            || (cashPaid > 0 && sale.PaidAmount - sale.ChangeAmount != sale.Total)
            || (cashPaid == 0 && sale.ChangeAmount != 0))
            throw new InvalidOperationException("El importe recibido y el cambio no coinciden con el cobro.");
        if (payments.Any(p => p.Amount <= 0 || CommercialLedger.Money(p.Amount) != p.Amount)
            || payments.GroupBy(p => p.Kind).Any(g => g.Count() > 1) || payments.Sum(p => p.Amount) != sale.Total)
            throw new InvalidOperationException("Los pagos confirmados deben sumar exactamente el total de la venta.");
        if (sale.GlobalDiscountAmount < 0 || CommercialLedger.Money(sale.GlobalDiscountAmount) != sale.GlobalDiscountAmount)
            throw new InvalidOperationException("Descuento global inválido.");
        if (sale.GlobalDiscountAmount > 0 && !recovering) await AuthorizeAsync(db, PermissionCatalog.PosDiscount);
        if (payments.Any(p => p.Kind == TenderKind.Credit) && !recovering) await AuthorizeAsync(db, PermissionCatalog.PosCreditSale);
        if (payments.Any(p => p.Kind == TenderKind.Loyalty) && !recovering) await AuthorizeAsync(db, PermissionCatalog.PosApplyPoints);

        var snapshots = new List<SaleItemSnapshotDto>();
        var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id) && p.IsActive)
            .Take(KitInventoryGraph.MaximumProducts + 1).ToDictionaryAsync(p => p.Id);
        var inventoryGraph = await KitInventoryGraph.LoadAsync(db, products.Values.ToList());
        var requirements = new Dictionary<string, decimal>();
        var promotionTargets = sale.Items.Where(i => i.AppliedPromotionId is not null)
            .Select(i => new PromotionTarget(i.ProductId, products.GetValueOrDefault(i.ProductId)?.Category, i.Quantity)).ToList();
        var activePromotions = await PromotionRuleReader.ReadApplicableAsync(db, promotionTargets);
        int stockMovements = 0;
        foreach (var item in sale.Items)
        {
            if (!double.IsFinite(item.Quantity) || item.Quantity <= 0 || Math.Round(item.Quantity, 3) != item.Quantity || item.UnitPrice < 0 || item.AppliedDiscountAmount < 0)
                throw new InvalidOperationException("Partida con cantidad o precio inválido.");
            var product = products.GetValueOrDefault(item.ProductId)
                ?? throw new InvalidOperationException("El producto ya no está activo.");
            if (!product.IsKit && product.Stock < item.Quantity) throw new InvalidOperationException("Stock insuficiente; actualice el carrito.");
            if (!product.IsBulk && item.Quantity != Math.Truncate(item.Quantity))
                throw new InvalidOperationException($"{product.Name} no admite cantidades fraccionarias.");
            var plan = inventoryGraph.RequirePlan(item.ProductId);
            stockMovements = checked(stockMovements + plan.Ingredients.Count);
            CommercialDocumentLimits.RequireStockMovementCount(stockMovements);
            foreach (var pair in inventoryGraph.QuantitiesFor(plan, (decimal)item.Quantity))
                requirements[pair.Key] = checked(requirements.GetValueOrDefault(pair.Key) + pair.Value);
            decimal expectedPrice = product.WholesaleThreshold > 0 && item.Quantity >= product.WholesaleThreshold && product.WholesalePrice > 0 ? product.WholesalePrice : product.Price;
            decimal basePrice = item.OriginalUnitPrice > 0 ? item.OriginalUnitPrice : item.UnitPrice;
            if (item.AppliedPromotionId is not null)
            {
                var candidate = new CartItemDto
                {
                    Id = product.Id,
                    Name = product.Name,
                    OriginalUnitPrice = basePrice,
                    UnitPrice = basePrice,
                    Category = product.Category
                };
                candidate.OverrideQuantity(item.Quantity, item.Quantity);
                var priced = PromotionService.Evaluate([candidate], activePromotions)[0];
                if (priced.AppliedPromotionId != item.AppliedPromotionId || priced.AppliedDiscountAmount != item.AppliedDiscountAmount)
                    throw new InvalidOperationException("La promoción cambió o no aplica. Actualice el carrito antes de cobrar.");
            }
            if (basePrice != expectedPrice && !recovering) await AuthorizeAsync(db, PermissionCatalog.PosModifyPrice);
            if (item.AppliedDiscountAmount > 0 && string.IsNullOrEmpty(item.AppliedPromotionId) && !recovering)
                await AuthorizeAsync(db, PermissionCatalog.PosDiscount);
            decimal lineTotal = CommercialLedger.Money(basePrice * (decimal)item.Quantity - item.AppliedDiscountAmount);
            if (lineTotal < 0 || lineTotal != item.TotalPrice)
                throw new InvalidOperationException("Los importes de la partida no coinciden con cantidad, precio y descuento.");
            snapshots.Add(item with
            {
                Name = product.Name,
                OriginalUnitPrice = basePrice,
                Cost = plan.UnitCost,
                Unit = product.Unit,
                SatProductCode = product.SatProductCode,
                SatUnitCode = product.SatUnitCode,
                PointsRewarded = product.PointsRewarded
            });
        }
        inventoryGraph.RequireAvailable(requirements);
        if (snapshots.Sum(i => i.TotalPrice) - sale.GlobalDiscountAmount != sale.Total)
            throw new InvalidOperationException("El total de la venta no coincide con sus partidas y descuentos.");
        snapshots = ApplyProratedGlobalDiscountAndTaxes(snapshots, sale.GlobalDiscountAmount);
        // Include canonical catalog text before authorizing a bank request or deducting stock.
        CommercialDocumentLimits.ValidateSale(sale with { Items = snapshots });
        CommercialDocumentLimits.RequireNewPayload(JsonSerializer.Serialize(sale with { Items = snapshots },
            Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto));
        return (snapshots, payments, inventoryGraph);
    }

    internal static List<SaleTenderDto> BuildPayments(SaleDto sale)
    {
        if (sale.Payments is not null) return sale.Payments;
        var result = new List<SaleTenderDto>();
        if (sale.Total == 0) return result;
        if (sale.CashAmount > 0) result.Add(new(TenderKind.Cash, sale.CashAmount));
        if (sale.CardAmount > 0) result.Add(new(TenderKind.Card, sale.CardAmount));
        if (sale.GiftcardAmount > 0) result.Add(new(TenderKind.Giftcard, sale.GiftcardAmount, AccountId: sale.GiftcardNumber));
        if (sale.IsCredit) result.Add(new(TenderKind.Credit, sale.Total - result.Sum(p => p.Amount), AccountId: sale.CustomerId));
        if (sale.PaymentMethod == "Puntos de Fidelidad") result.Add(new(TenderKind.Loyalty, sale.Total, AccountId: sale.CustomerId));
        if (sale.PaymentMethod is "Transferencia SPEI" or "CoDi / QR") result.Add(new(TenderKind.Transfer, sale.Total));
        if (result.Count == 0 && sale.PaymentMethod is "Cash" or "Efectivo") result.Add(new(TenderKind.Cash, sale.Total));
        return result;
    }

    private async Task PostPaymentAsync(AppDbContext db, OperationIdentity actor, SaleEntity sale, SaleTenderDto payment)
    {
        string? reference = payment.Reference;
        string? account = payment.AccountId;
        if (payment.Kind == TenderKind.Card)
        {
            var terminal = await db.PaymentTerminalOperations.AsNoTracking().SingleOrDefaultAsync(t => t.ExternalReference == sale.OperationId);
            if (!TerminalOrderEvidence.IsCaptured(terminal) || terminal!.Amount != payment.Amount)
                throw new InvalidOperationException("No existe un cargo de tarjeta confirmado por el importe exacto.");
            reference = terminal.ProviderPaymentId;
        }
        else if (payment.Kind == TenderKind.Transfer)
        {
            var confirmation = await IncomingTransferService.RequireProofAsync(db, actor, sale.OperationId, payment.Amount, IncomingTransferService.SalePurpose);
            reference = confirmation.ExternalReference;
            db.IncomingTransferReceipts.Add(IncomingTransferService.Receipt(actor, confirmation, sale.Id));
        }
        else if (payment.Kind == TenderKind.Cash)
            await CommercialLedger.CashAsync(db, actor, sale.OperationId, sale.Id, payment.Amount, false, MovementType.VentaEfectivo, $"Venta {sale.Id}");
        else if (payment.Kind == TenderKind.Credit)
        {
            account = sale.CustomerId ?? throw new InvalidOperationException("Asigne un cliente para crédito.");
            long cents = ToStoredCents(payment.Amount);
            if (await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE customers SET debt = debt + {cents}
                WHERE id = {account} AND IsActive = 1 AND is_credit_blocked = 0 AND credit_limit - debt >= {cents}") != 1)
                throw new InvalidOperationException("Cliente bloqueado o crédito insuficiente.");
            CommercialLedger.Account(db, actor, sale.OperationId, AccountKind.Customer, account, sale.Id, payment.Amount, "SALE_CREDIT");
            (await db.Shifts.SingleAsync(s => s.Id == actor.ShiftId)).TotalCreditSales += payment.Amount;
        }
        else if (payment.Kind == TenderKind.Giftcard)
        {
            account ??= sale.GiftcardNumber;
            if (string.IsNullOrWhiteSpace(account)) throw new InvalidOperationException("Indique el monedero.");
            long cents = ToStoredCents(payment.Amount);
            if (await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE giftcards SET balance = balance - {cents}
                WHERE card_number = {account} AND is_active = 1 AND balance >= {cents}") != 1)
                throw new InvalidOperationException("Monedero inactivo o saldo insuficiente.");
            CommercialLedger.Account(db, actor, sale.OperationId, AccountKind.Giftcard, account, sale.Id, -payment.Amount, "SALE_REDEMPTION");
        }
        else if (payment.Kind == TenderKind.Loyalty)
        {
            account = sale.CustomerId ?? throw new InvalidOperationException("Asigne un cliente para utilizar puntos.");
            double points = (double)payment.Amount;
            if (await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE customers SET puntos_saldo = puntos_saldo - {points}
                WHERE id = {account} AND IsActive = 1 AND puntos_saldo >= {points}") != 1)
                throw new InvalidOperationException("Puntos insuficientes.");
            CommercialLedger.Account(db, actor, sale.OperationId, AccountKind.Loyalty, account, sale.Id, -payment.Amount, "SALE_REDEMPTION");
        }
        db.SalePayments.Add(new SalePaymentEntity
        {
            SaleId = sale.Id,
            OperationId = sale.OperationId,
            Kind = payment.Kind,
            Amount = payment.Amount,
            ExternalReference = reference,
            AccountId = account,
            ShiftId = actor.ShiftId!,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId,
            CreatedAtUtc = sale.Date
        });
    }

    private static long ToStoredCents(decimal amount) => Convert.ToInt64(CommercialLedger.Money(amount) * 100m);

    public async Task<SaleResultModel> ProcessSaleAsync(SaleCreationDto dto)
    {
        try
        {
            if (dto.Items is null) throw new InvalidOperationException("La venta requiere partidas verificables.");
            CommercialDocumentLimits.RequireLineCount(dto.Items.Count);
            if (dto.Items.Any(i => i is null)) throw new InvalidOperationException("La venta requiere partidas verificables.");
            var snapshots = dto.Items.Select(i => new SaleItemSnapshotDto(i.Id, i.Name, i.UnitPrice, i.Cost, i.Quantity,
                i.Unit, i.Category, i.AppliedDiscountAmount, i.TotalPrice, i.OriginalUnitPrice, i.AppliedDiscountAmount, i.AppliedPromotionId)).ToList();
            bool credit = dto.CreditAmount > 0 || dto.PaymentMethod is PaymentMethod.Credito or PaymentMethod.Credit;
            var sale = new SaleDto(dto.OperationId ?? "", "", snapshots, dto.Total, 0m, 0m, dto.Total, 0m,
                dto.PaymentMethod.ToString(), dto.CustomerId, credit, false, null, OperationId: dto.OperationId,
                CashAmount: dto.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Efectivo ? dto.Total : 0m,
                CardAmount: dto.PaymentMethod is PaymentMethod.Card or PaymentMethod.Tarjeta ? dto.Total : 0m);
            var saved = await SaveAsync(sale); return new SaleResultModel { IsSuccess = true, SaleId = saved.Id };
        }
        catch (Exception ex) { return new SaleResultModel { IsSuccess = false, ErrorMessage = ex.Message }; }
    }

    public async Task<SaleDto?> GetByIdAsync(string saleId)
    {
        PurchaseDocumentReader.RequireKey(saleId, nameof(saleId));
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, PermissionCatalog.PosCheckout, false);
        var sale = await SaleDocumentReader.HeaderAsync(db, saleId);
        if (sale is not null && sale.CashierUserId != actor.UserId)
            await _operations.RequireAsync(db, PermissionCatalog.ReportsView, false);
        var result = sale is null ? null : await MapWithLedgersAsync(db, sale);
        if (await _operations.RequireAsync(db, PermissionCatalog.PosCheckout, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        return result;
    }
    public async Task<List<SaleDto>> GetHistoryAsync(int limit = 200)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit), "Utilice páginas de hasta 200 ventas.");
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ReportsView, false);
        BusinessDateQuery.RegisterOn(db, BusinessTime.Zone);
        var sales = await SaleDocumentReader.HeadersAsync(db, System.Runtime.CompilerServices.FormattableStringFactory.Create(
            $"SELECT rowid FROM sales ORDER BY ticketfy_business_ticks({SaleDocumentReader.SafeDate}) DESC, id COLLATE BINARY LIMIT {{0}}", limit));
        var result = await MapManyWithLedgersAsync(db, sales);
        if (await _operations.RequireAsync(db, PermissionCatalog.ReportsView, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        return result;
    }
    public async Task<List<SaleDto>> GetSalesByDateRangeAsync(DateTime start, DateTime end)
    {
        var page = await GetHistoryPageAsync(start, end, 0, 200);
        if (page.HasMore) throw new InvalidOperationException("El período supera 200 ventas. Utilice la consulta paginada del historial.");
        return page.Items;
    }
    public async Task<List<CashierPerformanceDto>> GetCashierPerformanceReportAsync(DateTime? startDate = null, DateTime? endDate = null, decimal defaultCommissionPct = 0m)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ReportsView, false);
        if (defaultCommissionPct < 0 || defaultCommissionPct > 100) throw new InvalidOperationException("Porcentaje de comisión inválido.");
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.FullName }).ToDictionaryAsync(u => u.Id, u => u.FullName);
        (string Id, string Name) Owner(string? value) => Guid.TryParse(value, out var id) && users.TryGetValue(id, out var name)
            ? (id.ToString(), name) : ("", "Histórico sin usuario verificable");
        var operators = new Dictionary<string, (string Name, int Tickets, decimal Revenue)>();
        await foreach (var sale in db.Sales.AsNoTracking().InCandidateDateRange(s => s.Date, startDate, endDate)
            .Select(s => new { s.Date, s.CashierUserId, s.Total, s.LedgerVersion, s.IsCancelled }).AsAsyncEnumerable())
        {
            if (!sale.Date.IsInDateRange(startDate, endDate)) continue;
            var (id, name) = Owner(sale.CashierUserId);
            (string Name, int Tickets, decimal Revenue) current = operators.GetValueOrDefault(id, (name, 0, 0m));
            operators[id] = (current.Name, checked(current.Tickets + 1),
                current.Revenue + (sale.LedgerVersion > 0 || sale.IsCancelled == 0 ? sale.Total : 0m));
        }
        var returns = from r in db.Returns.AsNoTracking().InCandidateDateRange(r => r.CreatedAt, startDate, endDate)
                      join s in db.Sales.AsNoTracking() on r.OriginalSaleId equals s.Id
                      where s.LedgerVersion > 0
                      select new { r.CreatedAt, r.TotalRefunded, s.CashierUserId };
        await foreach (var returned in returns.AsAsyncEnumerable())
        {
            if (!returned.CreatedAt.IsInDateRange(startDate, endDate)) continue;
            var (id, name) = Owner(returned.CashierUserId);
            (string Name, int Tickets, decimal Revenue) current = operators.GetValueOrDefault(id, (name, 0, 0m));
            operators[id] = (current.Name, current.Tickets, current.Revenue - returned.TotalRefunded);
        }
        return operators.OrderBy(pair => pair.Value.Name).ThenBy(pair => pair.Key).Select(pair =>
            new CashierPerformanceDto(pair.Value.Name, pair.Value.Tickets, pair.Value.Revenue,
                pair.Value.Tickets == 0 ? 0 : CommercialLedger.Money(pair.Value.Revenue / pair.Value.Tickets), pair.Key.Length == 0 ? 0 : defaultCommissionPct,
                pair.Key.Length == 0 ? 0 : CommercialLedger.Money(pair.Value.Revenue * defaultCommissionPct / 100m), AttributionVerified: pair.Key.Length > 0)).ToList();
    }
    private async Task<SaleDto> MapWithLedgersAsync(AppDbContext db, SaleEntity sale) =>
        (await MapManyWithLedgersAsync(db, [sale])).Single();

    private async Task<List<SaleDto>> MapManyWithLedgersAsync(AppDbContext db, List<SaleEntity> sales) =>
        await SaleDocumentReader.ReadAsync(db, sales, await SensitiveReadAccess.CanSeeCostsAsync(db, _operations));
}
