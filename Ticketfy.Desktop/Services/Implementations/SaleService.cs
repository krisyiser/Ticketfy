using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Models;
using Ticketfy.Core.Enums;
using Serilog;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Sale transaction service with atomic stock deduction and debt management.
/// Decomposed into partial classes: SaleService (Core), SaleService.Cancellation, SaleService.Inventory.
/// </summary>
public partial class SaleService : ISaleService
{
    public event Action<SaleDto>? SaleSaved;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly CoOccurrenceQueue _coOccurrenceQueue;
    private readonly Ticketfy.Services.Interfaces.IAuditService _auditService;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SaleService(IDbContextFactory<AppDbContext> contextFactory, CoOccurrenceQueue coOccurrenceQueue, Ticketfy.Services.Interfaces.IAuditService auditService)
    {
        _contextFactory = contextFactory;
        _coOccurrenceQueue = coOccurrenceQueue;
        _auditService = auditService;
    }

    public async Task<SaleDto> SaveAsync(SaleDto sale, FiscalOutboxRequest? fiscalOutbox = null)
    {
        if (sale.Items is null || sale.Items.Count == 0)
        {
            throw new InvalidOperationException("No se puede registrar una venta sin productos.");
        }

        using var _ctx = await _contextFactory.CreateDbContextAsync();
        await using var transaction = await _ctx.Database.BeginTransactionAsync();
        try
        {
            string operationId = string.IsNullOrWhiteSpace(sale.OperationId)
                ? (string.IsNullOrWhiteSpace(sale.Id) ? Guid.NewGuid().ToString("N") : sale.Id)
                : sale.OperationId.Trim();

            var existingSale = await _ctx.Sales
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.OperationId == operationId);
            if (existingSale is not null)
            {
                await transaction.RollbackAsync();
                return MapToDto(existingSale);
            }

            var saleId = sale.Id;
            if (string.IsNullOrEmpty(saleId) || !saleId.Contains("-") || saleId.Length != 11)
            {
                while (true)
                {
                    saleId = await GenerateNextSaleFolioAsync(_ctx);
                    if (!await _ctx.Sales.AnyAsync(s => s.Id == saleId))
                        break;
                }
            }

            // Global discount proration & VAT (IVA) calculation
            decimal snapshotsSum = sale.Items.Sum(i =>
                (Convert.ToDecimal(i.Quantity) * (i.OriginalUnitPrice > 0m ? i.OriginalUnitPrice : i.UnitPrice))
                - i.AppliedDiscountAmount);
            decimal globalDiscountAmount = Math.Max(0m, Math.Round(snapshotsSum - sale.Total, 2));
            var processedItems = ApplyProratedGlobalDiscountAndTaxes(sale.Items, globalDiscountAmount);

            var itemsJson = JsonSerializer.Serialize(
                processedItems,
                Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.ListSaleItemSnapshotDto);

            var total = Math.Round(sale.Total, 2);
            var totalCost = Math.Round(sale.TotalCost, 2);
            var profit = Math.Round(sale.Profit, 2);
            var paidAmount = Math.Round(sale.PaidAmount, 2);
            var changeAmount = Math.Round(sale.ChangeAmount, 2);

            var entity = new SaleEntity
            {
                Id = saleId,
                OperationId = operationId,
                Date = string.IsNullOrEmpty(sale.Date) ? DateTimeOffset.Now.ToString("o") : sale.Date,
                ItemsJson = itemsJson,
                Total = total,
                TotalCost = totalCost,
                Profit = profit,
                PaidAmount = paidAmount,
                ChangeAmount = changeAmount,
                PaymentMethod = sale.PaymentMethod,
                CustomerId = sale.CustomerId,
                IsCredit = sale.IsCredit ? 1 : 0,
                IsCancelled = 0,
                EstadoFiscal = sale.EstadoFiscal,
                SerieFolio = saleId,
                Status = SaleStatus.Completed,
                InvoiceId = sale.InvoiceId,
                InvoiceStatus = sale.InvoiceStatus,
                CashAmount = sale.CashAmount,
                CardAmount = sale.CardAmount,
                GiftcardAmount = sale.GiftcardAmount,
                GiftcardNumber = sale.GiftcardNumber,
                CommittedAtUtc = DateTimeOffset.UtcNow.ToString("o")
            };

            _ctx.Sales.Add(entity);

            // Deduct inventory recursively (Combo/Kit support) & compute true COGS
            decimal totalTicketCogs = 0m;
            foreach (var item in processedItems)
            {
                decimal unitCogs = await DeductInventoryRecursiveAsync(_ctx, item.ProductId, item.Quantity, new HashSet<string>());
                totalTicketCogs += unitCogs * Convert.ToDecimal(item.Quantity);
            }

            totalCost = Math.Round(totalTicketCogs, 2);
            profit = Math.Round(total - totalCost, 2);
            entity.TotalCost = totalCost;
            entity.Profit = profit;

            // If customer linked, validate credit constraints & update debt or accrue/deduct loyalty points
            if (!string.IsNullOrEmpty(sale.CustomerId))
            {
                var customer = await _ctx.Customers.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == sale.CustomerId);
                if (customer is not null)
                {
                    if (sale.IsCredit)
                    {
                        if (customer.IsCreditBlocked)
                        {
                            throw new InvalidOperationException($"El crédito está bloqueado para el cliente: {customer.Name}");
                        }

                        long creditRequiredCents = ToStoredCents(total);
                        int updated = await _ctx.Database.ExecuteSqlInterpolatedAsync($@"
                            UPDATE customers
                            SET debt = debt + {creditRequiredCents}
                            WHERE id = {sale.CustomerId}
                              AND is_credit_blocked = 0
                              AND credit_limit - debt >= {creditRequiredCents};");
                        if (updated != 1)
                        {
                            decimal availableCredit = await _ctx.Customers.AsNoTracking()
                                .Where(c => c.Id == sale.CustomerId)
                                .Select(c => c.CreditLimit - c.Debt)
                                .SingleAsync();
                            throw new InvalidOperationException($"Crédito insuficiente o modificado por otra operación. Disponible: {availableCredit:C}, Requerido: {total:C}");
                        }
                    }

                    if (sale.PaymentMethod == "Puntos de Fidelidad")
                    {
                        double pointsToRedeem = Convert.ToDouble(total);
                        await _ctx.Database.ExecuteSqlInterpolatedAsync($@"
                            UPDATE customers
                            SET puntos_saldo = MAX(0, puntos_saldo - {pointsToRedeem})
                            WHERE id = {sale.CustomerId};");
                    }
                    else
                    {
                        double earnedPoints = processedItems.Sum(i => i.PointsRewarded * i.Quantity);
                        if (earnedPoints != 0d)
                        {
                            await _ctx.Database.ExecuteSqlInterpolatedAsync($@"
                                UPDATE customers
                                SET puntos_saldo = puntos_saldo + {earnedPoints}
                                WHERE id = {sale.CustomerId};");
                        }
                    }
                }
                else if (sale.IsCredit)
                {
                    throw new InvalidOperationException("Cliente no encontrado en la base de datos.");
                }
            }
            else if (sale.IsCredit)
            {
                throw new InvalidOperationException("Debe asignar un cliente registrado para cobrar a crédito.");
            }

            if (sale.GiftcardAmount > 0m)
            {
                if (string.IsNullOrWhiteSpace(sale.GiftcardNumber))
                    throw new InvalidOperationException("Debe indicar el monedero utilizado.");

                long giftcardAmountCents = ToStoredCents(sale.GiftcardAmount);
                int updated = await _ctx.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE giftcards
                    SET balance = balance - {giftcardAmountCents}
                    WHERE card_number = {sale.GiftcardNumber}
                      AND is_active = 1
                      AND balance >= {giftcardAmountCents};");
                if (updated != 1)
                    throw new InvalidOperationException("Monedero no válido, sin fondos suficientes o modificado por otra operación.");
            }

            // Caja is part of the same commit as sale, stock, debt and journal.
            var activeShift = await _ctx.Shifts.FirstOrDefaultAsync(s => s.IsOpen == 1);
            if (activeShift is not null)
            {
                if (sale.IsCredit)
                {
                    activeShift.TotalCreditSales = Math.Round(activeShift.TotalCreditSales + total, 2);
                }

                // CashAmount is the cash allocation retained by the drawer after
                // change, not the amount handed over by the customer.
                decimal netCashIn = Math.Max(0m, Math.Round(sale.CashAmount, 2));
                if (netCashIn > 0m)
                {
                    activeShift.TotalCashSales = Math.Round(activeShift.TotalCashSales + netCashIn, 2);
                    activeShift.ExpectedBalance = Math.Round(activeShift.ExpectedBalance + netCashIn, 2);
                    _ctx.ShiftMovements.Add(new ShiftMovementEntity
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ShiftId = activeShift.Id,
                        MovementType = MovementType.VentaEfectivo,
                        Amount = netCashIn,
                        IsOutflow = false,
                        Description = $"Venta {saleId}",
                        ReferenceId = saleId,
                        Timestamp = DateTimeOffset.UtcNow.ToString("O")
                    });
                }
            }

            var checkoutOperation = await _ctx.CheckoutOperations.FindAsync(operationId);
            if (checkoutOperation is not null)
            {
                checkoutOperation.SaleId = saleId;
                checkoutOperation.State = CheckoutOperationState.Completed;
                checkoutOperation.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o");
                checkoutOperation.FailureReason = null;
            }

            _ctx.TransactionJournal.Add(new TransactionJournalEntryEntity
            {
                OperationId = operationId,
                EventType = "SALE_COMPLETED",
                EntityId = saleId,
                Amount = total,
                Details = sale.PaymentMethod
            });

            if (fiscalOutbox is not null)
            {
                if (string.IsNullOrWhiteSpace(fiscalOutbox.IdempotencyKey) || fiscalOutbox.IdempotencyKey.Length > 100)
                    throw new InvalidOperationException("Clave de idempotencia fiscal inválida.");
                fiscalOutbox.Request.Folio ??= new string(saleId.Where(char.IsLetterOrDigit).Take(40).ToArray());
                fiscalOutbox.Request.OrderNumber ??= fiscalOutbox.IdempotencyKey[..Math.Min(fiscalOutbox.IdempotencyKey.Length, 100)];
                string queuedAt = DateTimeOffset.UtcNow.ToString("O");
                _ctx.FiscalOperations.Add(new FiscalOperationEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IdempotencyKey = fiscalOutbox.IdempotencyKey,
                    SaleId = saleId,
                    OperationType = FiscalOperationType.Issue,
                    Status = IntegrationOperationStatus.Pending,
                    PayloadJson = JsonSerializer.Serialize(fiscalOutbox.Request, FacturamaJsonContext.Default.FacturamaCfdiRequest),
                    NextAttemptAtUtc = queuedAt,
                    CreatedAtUtc = queuedAt,
                    UpdatedAtUtc = queuedAt
                });
                entity.EstadoFiscal = "EN COLA";
            }

            try
            {
                await DbResilienceHelper.ExecuteWithRetryAsync(async () =>
                {
                    await _ctx.SaveChangesAsync();
                    await transaction.CommitAsync();
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync();
                throw new DbUpdateConcurrencyException("El inventario cambió durante la transacción. Intente cobrar de nuevo.");
            }

            var savedResult = sale with
            {
                Id = saleId,
                Items = processedItems,
                Total = total,
                TotalCost = totalCost,
                Profit = profit,
                PaidAmount = paidAmount,
                ChangeAmount = changeAmount,
                SerieFolio = saleId,
                OperationId = operationId,
                GiftcardNumber = sale.GiftcardNumber,
                GiftcardAmount = sale.GiftcardAmount,
                Status = SaleStatus.Completed
            };

            SaleSaved?.Invoke(savedResult);
            return savedResult;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static long ToStoredCents(decimal amount) =>
        Convert.ToInt64(Math.Round(amount, 2, MidpointRounding.ToEven) * 100m);

    public async Task<SaleResultModel> ProcessSaleAsync(SaleCreationDto dto)
    {
        bool isCredit = dto.CreditAmount > 0 || dto.PaymentMethod == PaymentMethod.Credito || dto.PaymentMethod == PaymentMethod.Credit;
        var snapshots = dto.Items.Select(i => new SaleItemSnapshotDto(
            ProductId: i.Id,
            Name: i.Name,
            UnitPrice: i.UnitPrice,
            Cost: Math.Round(i.UnitPrice * 0.6m, 2),
            Quantity: i.Quantity,
            Unit: i.Unit,
            Category: i.Category ?? "General",
            Discount: i.AppliedDiscountAmount,
            TotalPrice: i.TotalPrice,
            OriginalUnitPrice: i.OriginalUnitPrice > 0 ? i.OriginalUnitPrice : i.UnitPrice,
            AppliedDiscountAmount: i.AppliedDiscountAmount,
            AppliedPromotionId: i.AppliedPromotionId
        )).ToList();

        var totalCost = snapshots.Sum(s => s.Cost * Convert.ToDecimal(s.Quantity));
        var profit = dto.Total - totalCost;

        var saleDto = new SaleDto(
            Id: IdGenerator.NewSaleId(),
            Date: DateTimeOffset.Now.ToString("o"),
            Items: snapshots,
            Total: dto.Total,
            TotalCost: totalCost,
            Profit: profit,
            PaidAmount: dto.Total,
            ChangeAmount: 0m,
            PaymentMethod: dto.PaymentMethod.ToString(),
            CustomerId: dto.CustomerId,
            IsCredit: isCredit,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            UuidSat: null,
            SerieFolio: null,
            OperationId: dto.OperationId
        );

        try
        {
            var saved = await SaveAsync(saleDto);
            return new SaleResultModel { IsSuccess = true, SaleId = saved.Id };
        }
        catch (Exception ex)
        {
            return new SaleResultModel { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<SaleDto?> GetByIdAsync(string saleId)
    {
        using var _ctx = await _contextFactory.CreateDbContextAsync();
        var entity = await _ctx.Sales
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == saleId);

        if (entity == null) return null;
        return MapToDto(entity);
    }

    public async Task<List<SaleDto>> GetHistoryAsync(int limit = 500)
    {
        using var _ctx = await _contextFactory.CreateDbContextAsync();
        var entities = await _ctx.Sales
            .AsNoTracking()
            .OrderByDescending(s => s.Date)
            .Take(limit)
            .ToListAsync();

        return entities.Select(MapToDto).ToList();
    }

    public async Task<List<SaleDto>> GetSalesByDateRangeAsync(DateTime start, DateTime end)
    {
        using var _ctx = await _contextFactory.CreateDbContextAsync();
        string startIso = start.ToString("yyyy-MM-dd");
        string endIso = end.AddDays(1).ToString("yyyy-MM-dd");

        var entities = await _ctx.Sales
            .AsNoTracking()
            .Where(s => string.Compare(s.Date, startIso) >= 0 && string.Compare(s.Date, endIso) <= 0)
            .OrderByDescending(s => s.Date)
            .ToListAsync();

        var filtered = entities
            .Where(s => s.Date.IsInDateRange(start, end))
            .ToList();

        return filtered.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Generates date-bounded cashier performance analytics with SQL-level projection to prevent memory leaks and unbounded data dumps.
    /// </summary>
    public async Task<List<CashierPerformanceDto>> GetCashierPerformanceReportAsync(DateTime? startDate = null, DateTime? endDate = null, decimal defaultCommissionPct = 0m)
    {
        try
        {
            using var _ctx = await _contextFactory.CreateDbContextAsync();
            var query = _ctx.Sales
                .AsNoTracking()
                .Where(s => s.IsCancelled == 0);

            if (startDate.HasValue)
            {
                string startIso = startDate.Value.ToString("yyyy-MM-dd");
                query = query.Where(s => string.Compare(s.Date, startIso) >= 0);
            }

            if (endDate.HasValue)
            {
                string endIso = endDate.Value.AddDays(1).ToString("yyyy-MM-dd");
                query = query.Where(s => string.Compare(s.Date, endIso) <= 0);
            }

            var allSales = await query.ToListAsync();

            var validSales = allSales
                .Where(s => s.Date.IsInDateRange(startDate, endDate))
                .ToList();
            var users = await _ctx.Users.AsNoTracking().ToListAsync();
            var report = new List<CashierPerformanceDto>();

            var cashiers = users.Where(u => u.Role == Core.Enums.UserRole.Cajero).ToList();
            if (cashiers.Count == 0 && users.Count > 0)
            {
                cashiers = users;
            }

            if (cashiers.Count > 0)
            {
                // Attribute total period sales to the operational cashier(s) cleanly
                int count = validSales.Count;
                decimal totalRev = validSales.Sum(s => s.Total);
                decimal avgTicket = count > 0 ? totalRev / count : 0m;
                decimal commission = totalRev * (defaultCommissionPct / 100m);

                foreach (var cashier in cashiers)
                {
                    report.Add(new CashierPerformanceDto(
                        cashier.FullName,
                        count,
                        Math.Round(totalRev, 2),
                        Math.Round(avgTicket, 2),
                        defaultCommissionPct,
                        Math.Round(commission, 2)
                    ));
                }
            }
            else
            {
                int count = validSales.Count;
                decimal totalRev = validSales.Sum(s => s.Total);
                decimal avgTicket = count > 0 ? totalRev / count : 0m;
                decimal commission = totalRev * (defaultCommissionPct / 100m);

                report.Add(new CashierPerformanceDto(
                    "CAJERO MATRIZ",
                    count,
                    Math.Round(totalRev, 2),
                    Math.Round(avgTicket, 2),
                    defaultCommissionPct,
                    Math.Round(commission, 2)
                ));
            }

            return report;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error generating cashier performance report");
            return [];
        }
    }


    private static SaleDto MapToDto(SaleEntity e)
    {
        var items = JsonSerializer.Deserialize(
            e.ItemsJson,
            Ticketfy.Desktop.Core.Helpers.TicketfyJsonContext.Default.ListSaleItemSnapshotDto) ?? [];
        return new SaleDto(
            Id: e.Id,
            Date: e.Date,
            Items: items,
            Total: e.Total,
            TotalCost: e.TotalCost,
            Profit: e.Profit,
            PaidAmount: e.PaidAmount,
            ChangeAmount: e.ChangeAmount,
            PaymentMethod: e.PaymentMethod,
            CustomerId: e.CustomerId,
            IsCredit: e.IsCredit == 1,
            IsCancelled: e.IsCancelled == 1,
            CancelledAt: e.CancelledAt,
            EstadoFiscal: e.EstadoFiscal,
            UuidSat: e.UuidSat,
            SerieFolio: e.SerieFolio,
            Status: e.Status,
            InvoiceId: e.InvoiceId,
            InvoiceStatus: e.InvoiceStatus,
            CashierUserId: null,
            CashierName: null,
            CashAmount: e.CashAmount,
            CardAmount: e.CardAmount,
            OperationId: e.OperationId,
            GiftcardNumber: e.GiftcardNumber,
            GiftcardAmount: e.GiftcardAmount
        );
    }
}
