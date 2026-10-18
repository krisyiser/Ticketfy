using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Models;
using Ticketfy.Core.Enums;
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
        string operationId = CommercialLedger.OperationId(sale.OperationId ?? sale.Id);
        if (sale.Items is null || sale.Items.Count == 0 || sale.Total < 0 || CommercialLedger.Money(sale.Total) != sale.Total)
            throw new InvalidOperationException("La venta requiere partidas e importe válido.");
        await using var db = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        OperationIdentity actor;
        if (recovering)
        {
            var pending = await db.CheckoutOperations.AsNoTracking().SingleAsync(o => o.OperationId == operationId);
            var shift = await db.Shifts.SingleOrDefaultAsync(s => s.Id == sale.ShiftId && s.RegisterId == sale.RegisterId && s.CashierUserId == sale.CashierUserId && s.IsOpen == 1);
            if (pending.State != CheckoutOperationState.PaymentAuthorized || shift is null || string.IsNullOrEmpty(sale.CashierUserId))
                throw new InvalidOperationException("El cobro requiere conciliación en su caja y turno originales.");
            var recoveredUser = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == Guid.Parse(sale.CashierUserId));
            var recoveryPermissions = new Ticketfy.Core.Services.UserRolePermissionEngine(new SettingsService(_contextFactory));
            if (recoveredUser is null || !recoveredUser.IsActive || !await recoveryPermissions.HasPermissionAsync(
                    recoveryPermissions.MapToModel(recoveredUser).DisplayRole, PermissionCatalog.PosCheckout))
                throw new UnauthorizedAccessException("El operador original ya no está autorizado; requiere conciliación supervisada.");
            actor = new OperationIdentity(sale.CashierUserId, sale.RegisterId!, shift.Id);
        }
        else actor = await AuthorizeAsync(db, PermissionCatalog.PosCheckout);
        var intent = await db.CheckoutOperations.AsNoTracking().SingleOrDefaultAsync(o => o.OperationId == operationId);
        if (intent is not null)
        {
            var original = JsonSerializer.Deserialize(intent.SalePayload, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.SaleDto);
            if (original is null || CommandIdentity.Sale(original) != CommandIdentity.Sale(sale)
                || intent.CashierUserId != actor.UserId || intent.ShiftId != actor.ShiftId || intent.RegisterId != actor.RegisterId)
                throw new InvalidOperationException("El intento de cobro pertenece a otros datos, usuario o turno.");
        }
        if (await db.IncomingTransferIntents.AnyAsync(i => i.OperationId == operationId))
            throw new InvalidOperationException("La clave pertenece a un abono o recarga, no a una venta.");
        await CommandIdentity.BindAsync(db, actor, operationId, "SALE", CommandIdentity.Sale(sale));
        var existing = await db.Sales.AsNoTracking().SingleOrDefaultAsync(s => s.OperationId == operationId);
        if (existing is not null)
        {
            if (existing.CashierUserId != actor.UserId || existing.Total != sale.Total)
                throw new InvalidOperationException("La clave de operación ya fue utilizada para otro cobro.");
            return await MapWithLedgersAsync(db, existing);
        }
        var (snapshots, payments) = await PrepareSaleAsync(db, sale, recovering);
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
            decimal cost = await DeductInventoryRecursiveAsync(db, item.ProductId, item.Quantity, new HashSet<string>(), actor, operationId, saleId, line.Id);
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == item.ProductId);
            line.UnitCost = cost;
            line.CostTotal = CommercialLedger.Money(cost * line.Quantity);
            line.Tax = CommercialLedger.Money(line.Total - line.Total / (1 + product.TaxRate));
            db.SaleLines.Add(line);
            snapshots[i] = item with { Cost = cost, TaxAmount = line.Tax, SaleLineId = line.Id };
        }
        entity.TotalCost = db.SaleLines.Local.Where(l => l.SaleId == saleId).Sum(l => l.CostTotal);
        entity.Profit = entity.Total - entity.TotalCost;
        entity.ItemsJson = JsonSerializer.Serialize(snapshots, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.ListSaleItemSnapshotDto);
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
        var checkout = await db.CheckoutOperations.FindAsync(operationId);
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
        await transaction.CommitAsync();
        _coOccurrenceQueue.Queue.Writer.TryWrite(snapshots.Select(i => i.ProductId).Distinct().ToList());
        var result = await MapWithLedgersAsync(db, entity);
        try { SaleSaved?.Invoke(result); } catch (Exception ex) { Serilog.Log.Error(ex, "Sale notification failed after commit {SaleId}", saleId); }
        return result;
    }


    private async Task<(List<SaleItemSnapshotDto> Items, List<SaleTenderDto> Payments)> PrepareSaleAsync(AppDbContext db, SaleDto sale, bool recovering = false)
    {
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
        string nowIso = DateTime.Now.ToString("s");
        var activePromotions = await db.Promotions.AsNoTracking().Where(p => p.IsActive == 1
            && string.Compare(p.StartDate, nowIso) <= 0 && string.Compare(p.EndDate, nowIso) >= 0)
            .OrderByDescending(p => p.Priority).ThenBy(p => p.Id).ToListAsync();
        foreach (var item in sale.Items)
        {
            if (!double.IsFinite(item.Quantity) || item.Quantity <= 0 || Math.Round(item.Quantity, 3) != item.Quantity || item.UnitPrice < 0 || item.AppliedDiscountAmount < 0)
                throw new InvalidOperationException("Partida con cantidad o precio inválido.");
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == item.ProductId && p.IsActive)
                ?? throw new InvalidOperationException("El producto ya no está activo.");
            if (!product.IsKit && product.Stock < item.Quantity) throw new InvalidOperationException("Stock insuficiente; actualice el carrito.");
            if (!product.IsBulk && item.Quantity != Math.Truncate(item.Quantity))
                throw new InvalidOperationException($"{product.Name} no admite cantidades fraccionarias.");
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
                Cost = product.Cost,
                Unit = product.Unit,
                SatProductCode = product.SatProductCode,
                SatUnitCode = product.SatUnitCode,
                PointsRewarded = product.PointsRewarded
            });
        }
        if (snapshots.Sum(i => i.TotalPrice) - sale.GlobalDiscountAmount != sale.Total)
            throw new InvalidOperationException("El total de la venta no coincide con sus partidas y descuentos.");
        snapshots = ApplyProratedGlobalDiscountAndTaxes(snapshots, sale.GlobalDiscountAmount);
        return (snapshots, payments);
    }

    private static List<SaleTenderDto> BuildPayments(SaleDto sale)
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
            if (terminal is null || terminal.Status != IntegrationOperationStatus.Completed || terminal.Amount != payment.Amount || string.IsNullOrWhiteSpace(terminal.ProviderPaymentId))
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
        var snapshots = dto.Items.Select(i => new SaleItemSnapshotDto(i.Id, i.Name, i.UnitPrice, i.Cost, i.Quantity,
            i.Unit, i.Category, i.AppliedDiscountAmount, i.TotalPrice, i.OriginalUnitPrice, i.AppliedDiscountAmount, i.AppliedPromotionId)).ToList();
        bool credit = dto.CreditAmount > 0 || dto.PaymentMethod is PaymentMethod.Credito or PaymentMethod.Credit;
        var sale = new SaleDto(dto.OperationId ?? "", "", snapshots, dto.Total, 0m, 0m, dto.Total, 0m,
            dto.PaymentMethod.ToString(), dto.CustomerId, credit, false, null, OperationId: dto.OperationId,
            CashAmount: dto.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Efectivo ? dto.Total : 0m,
            CardAmount: dto.PaymentMethod is PaymentMethod.Card or PaymentMethod.Tarjeta ? dto.Total : 0m);
        try { var saved = await SaveAsync(sale); return new SaleResultModel { IsSuccess = true, SaleId = saved.Id }; }
        catch (Exception ex) { return new SaleResultModel { IsSuccess = false, ErrorMessage = ex.Message }; }
    }

    public async Task<SaleDto?> GetByIdAsync(string saleId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var sale = await db.Sales.AsNoTracking().SingleOrDefaultAsync(s => s.Id == saleId);
        return sale is null ? null : await MapWithLedgersAsync(db, sale);
    }
    public async Task<List<SaleDto>> GetHistoryAsync(int limit = 500)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ReportsView, false);
        var sales = await db.Sales.AsNoTracking().OrderByDescending(s => s.Date).Take(Math.Clamp(limit, 1, 5000)).ToListAsync();
        var result = new List<SaleDto>();
        foreach (var sale in sales) result.Add(await MapWithLedgersAsync(db, sale));
        return result;
    }
    public async Task<List<SaleDto>> GetSalesByDateRangeAsync(DateTime start, DateTime end)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ReportsView, false);
        var sales = await db.Sales.AsNoTracking().ToListAsync();
        var result = new List<SaleDto>();
        foreach (var sale in sales.Where(s => s.Date.IsInDateRange(start, end))) result.Add(await MapWithLedgersAsync(db, sale));
        return result;
    }
    public async Task<List<CashierPerformanceDto>> GetCashierPerformanceReportAsync(DateTime? startDate = null, DateTime? endDate = null, decimal defaultCommissionPct = 0m)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.ReportsView, false);
        if (defaultCommissionPct < 0 || defaultCommissionPct > 100) throw new InvalidOperationException("Porcentaje de comisión inválido.");
        var allSales = await db.Sales.AsNoTracking().ToListAsync();
        var ledgerIds = allSales.Where(s => s.LedgerVersion > 0).Select(s => s.Id).ToHashSet();
        var sales = allSales.Where(s => s.Date.IsInDateRange(startDate, endDate)).ToList();
        var returns = (await db.Returns.AsNoTracking().ToListAsync()).Where(r => r.CreatedAt.IsInDateRange(startDate, endDate)).ToList();
        return allSales.GroupBy(s => s.CashierUserId).Select(g =>
        {
            var ids = g.Select(s => s.Id).ToHashSet();
            var period = sales.Where(s => ids.Contains(s.Id)).ToList();
            decimal revenue = period.Where(s => s.LedgerVersion > 0 || s.IsCancelled == 0).Sum(s => s.Total)
                - returns.Where(r => ledgerIds.Contains(r.OriginalSaleId) && ids.Contains(r.OriginalSaleId)).Sum(r => r.TotalRefunded);
            return new CashierPerformanceDto(g.First().CashierName ?? "Histórico sin atribución", period.Count, revenue,
                period.Count == 0 ? 0 : CommercialLedger.Money(revenue / period.Count), defaultCommissionPct,
                CommercialLedger.Money(revenue * defaultCommissionPct / 100m));
        }).ToList();
    }
    private static async Task<SaleDto> MapWithLedgersAsync(AppDbContext db, SaleEntity e)
    {
        var items = JsonSerializer.Deserialize(e.ItemsJson, Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.ListSaleItemSnapshotDto) ?? [];
        var lines = await db.SaleLines.AsNoTracking().Where(l => l.SaleId == e.Id).OrderBy(l => l.LineNumber).ToListAsync();
        var ids = lines.Select(l => l.Id).ToArray();
        var returned = await db.ReturnLines.AsNoTracking().Where(r => ids.Contains(r.SaleLineId)).ToListAsync();
        for (int i = 0; i < items.Count && i < lines.Count; i++)
            items[i] = items[i] with { SaleLineId = lines[i].Id, ReturnedQuantity = (double)returned.Where(r => r.SaleLineId == lines[i].Id).Sum(r => r.Quantity) };
        var payments = await db.SalePayments.AsNoTracking().Where(p => p.SaleId == e.Id).ToListAsync();
        return new SaleDto(e.Id, e.Date, items, e.Total, e.TotalCost, e.Profit, e.PaidAmount, e.ChangeAmount, e.PaymentMethod,
            e.CustomerId, e.IsCredit == 1, e.IsCancelled == 1, e.CancelledAt, e.EstadoFiscal, e.UuidSat, e.SerieFolio, e.Status,
            e.InvoiceId, e.InvoiceStatus, e.CashierUserId, e.CashierName, e.CashAmount, e.CardAmount, e.OperationId,
            e.GiftcardNumber, e.GiftcardAmount, e.ShiftId, e.RegisterId,
            Payments: payments.Select(p => new SaleTenderDto(p.Kind, p.Amount, p.ExternalReference, p.AccountId)).ToList(),
            RefundedAmount: returned.Sum(r => r.Amount));
    }
}
