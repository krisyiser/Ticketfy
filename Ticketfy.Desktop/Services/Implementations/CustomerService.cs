using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.Services.Implementations;

public sealed class CustomerService : ICustomerService
{
    private readonly AppDbContext _ctx;
    private readonly IEscPosPrinterService? _printerService;

    public CustomerService(AppDbContext ctx, IEscPosPrinterService? printerService = null)
    {
        _ctx = ctx;
        _printerService = printerService;
    }

    public async Task<List<CustomerDto>> GetAllAsync()
    {
        var list = await _ctx.Customers
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync();

        return list.Select(c => new CustomerDto(
            c.Id, c.Name, c.Phone ?? "", c.Email ?? "", c.Rfc ?? "", c.CreditLimit, c.Debt, c.PuntosSaldo, 0m, c.CustomerCode ?? ""
        )).ToList();
    }

    public async Task<CustomerDto?> GetByIdAsync(string id)
    {
        var c = await _ctx.Customers.FindAsync(id);
        return c is null ? null : new CustomerDto(
            c.Id, c.Name, c.Phone ?? "", c.Email ?? "", c.Rfc ?? "", c.CreditLimit, c.Debt, c.PuntosSaldo, 0m, c.CustomerCode ?? ""
        );
    }

    public async Task AddAsync(CustomerDto customer)
    {
        _ctx.Customers.Add(new CustomerEntity
        {
            Id = string.IsNullOrEmpty(customer.Id) ? IdGenerator.NewCustomerId() : customer.Id,
            Name = customer.Name,
            Phone = customer.Phone,
            Email = customer.Email,
            Rfc = customer.Rfc,
            CreditLimit = customer.CreditLimit,
            Debt = customer.Debt,
            PuntosSaldo = customer.PuntosSaldo,
            CustomerCode = string.IsNullOrWhiteSpace(customer.CustomerCode) ? $"CLI-{Random.Shared.Next(1000, 9999)}" : customer.CustomerCode
        });
        await _ctx.SaveChangesAsync();
    }

    public async Task UpdateAsync(CustomerDto customer)
    {
        var c = await _ctx.Customers.FindAsync(customer.Id);
        if (c is not null)
        {
            c.Name = customer.Name;
            c.Phone = customer.Phone;
            c.Email = customer.Email;
            c.Rfc = customer.Rfc;
            c.CreditLimit = customer.CreditLimit;
            c.Debt = customer.Debt;
            c.CustomerCode = customer.CustomerCode;
            _ctx.Customers.Update(c);
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task UpdateDebtAsync(string customerId, decimal newDebt)
    {
        var c = await _ctx.Customers.FindAsync(customerId);
        if (c is not null)
        {
            c.Debt = Math.Max(0m, Math.Round(newDebt, 2));
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task AddPaymentAsync(CustomerPaymentDto payment)
    {
        await RegisterCustomerPaymentAsync(payment.CustomerId, payment.Amount, payment.Method ?? "Efectivo", payment.Notes ?? "", payment.Id);
    }

    public async Task<bool> RegisterCustomerPaymentAsync(string customerId, decimal amount, string method, string notes, string? operationId = null)
    {
        if (amount <= 0)
            throw new InvalidOperationException("El abono debe ser mayor a $0.00.");

        operationId = string.IsNullOrWhiteSpace(operationId) ? Guid.NewGuid().ToString("N") : operationId.Trim();
        await using var transaction = await _ctx.Database.BeginTransactionAsync();
        try
        {
            if (await _ctx.CustomerPayments.AsNoTracking().AnyAsync(p => p.OperationId == operationId))
            {
                await transaction.RollbackAsync();
                return true;
            }

            // This service can be long-lived in the UI. Detach an older cached
            // customer so a raw atomic balance update cannot later be overwritten
            // by stale change-tracker state.
            var trackedCustomer = _ctx.Customers.Local.FirstOrDefault(c => c.Id == customerId);
            if (trackedCustomer is not null)
                _ctx.Entry(trackedCustomer).State = EntityState.Detached;

            var customer = await _ctx.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId)
                ?? throw new InvalidOperationException("Cliente no encontrado.");

            var roundedAmount = Math.Round(amount, 2);
            long amountCents = Convert.ToInt64(roundedAmount * 100m);

            // 1. DEDUCT CUSTOMER DEBT ATOMICALLY. The database stores monetary
            // values as integer cents, so distinct concurrent payments cannot
            // overwrite each other's balance update.
            int debtRows = await _ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE customers
                SET debt = debt - {amountCents}
                WHERE id = {customerId}
                  AND debt >= {amountCents};");
            if (debtRows != 1)
                throw new InvalidOperationException("El abono excede la deuda actual o la deuda cambió por otra operación.");

            // 2. IDENTIFY ACTIVE CASHIER SHIFT
            var activeShift = await _ctx.Shifts.FirstOrDefaultAsync(s => s.IsOpen == 1);

            // 3. CREATE PAYMENT RECORD WITH SHIFT BINDING
            var paymentRecord = new CustomerPaymentEntity
            {
                Id = IdGenerator.NewPaymentId(),
                OperationId = operationId,
                CustomerId = customerId,
                ShiftId = activeShift?.Id,
                Date = DateTimeOffset.Now.ToString("o"),
                Amount = roundedAmount,
                Method = method,
                Notes = notes,
                Status = Ticketfy.Core.Enums.PaymentStatus.Completed
            };
            _ctx.CustomerPayments.Add(paymentRecord);

            // 4. INJECT PHYSICAL MONEY INTO SHIFT CASH LEDGER IF PAID IN CASH
            bool isCash = method.Equals("Efectivo", StringComparison.OrdinalIgnoreCase) || method.Equals("Cash", StringComparison.OrdinalIgnoreCase);

            if (activeShift != null && isCash)
            {
                activeShift.ExpectedBalance = Math.Round(activeShift.ExpectedBalance + roundedAmount, 2);
                var cashMovement = new ShiftMovementEntity
                {
                    ShiftId = activeShift.Id,
                    MovementType = Ticketfy.Core.Enums.MovementType.AbonoCliente,
                    Amount = roundedAmount,
                    ReferenceId = paymentRecord.Id,
                    Description = $"Abono a deuda - Cliente: {customer.Name}",
                    Timestamp = DateTimeOffset.Now.ToString("o")
                };
                _ctx.ShiftMovements.Add(cashMovement);
            }

            _ctx.TransactionJournal.Add(new TransactionJournalEntryEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                OperationId = operationId,
                EventType = "CUSTOMER_PAYMENT_COMPLETED",
                EntityId = paymentRecord.Id,
                Amount = roundedAmount,
                Details = $"Cliente={customerId}; Metodo={method}",
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            });

            await _ctx.SaveChangesAsync();
            await transaction.CommitAsync();

            // 5. TRIGGER PHYSICAL DRAWER KICK AND AUDIT SLIP
            if (isCash && _printerService != null)
            {
                _ = _printerService.OpenCashDrawerAsync("COM1");
                _ = _printerService.PrintNonSaleCashMovementSlipAsync(new Ticketfy.Core.Models.ShiftMovementSlipModel
                {
                    Folio = paymentRecord.Id.Substring(0, Math.Min(8, paymentRecord.Id.Length)).ToUpper(),
                    MovementTypeLabel = "ABONO DE CLIENTE - CUENTA CORRIENTE",
                    Amount = roundedAmount,
                    Description = $"Abono a cuenta corriente de {customer.Name}. Notas: {notes}",
                    CashierName = "CAJERO EN TURNO",
                    Timestamp = DateTime.Now
                });
            }

            return true;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<List<CustomerPaymentDto>> GetPaymentsAsync(string customerId)
    {
        var list = await _ctx.CustomerPayments
            .AsNoTracking()
            .Where(p => p.CustomerId == customerId)
            .OrderByDescending(p => p.Date)
            .ToListAsync();

        return list.Select(p => new CustomerPaymentDto(p.Id, p.CustomerId, p.Date, p.Amount, p.Method ?? "Efectivo", p.Notes ?? "")).ToList();
    }

    public async Task DeleteAsync(string id)
    {
        var c = await _ctx.Customers.FindAsync(id);
        if (c is not null)
        {
            _ctx.Customers.Remove(c);
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task UpdatePointsAsync(string customerId, double points)
    {
        var c = await _ctx.Customers.FindAsync(customerId);
        if (c is not null)
        {
            c.PuntosSaldo = points;
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task<FiscalClientDto?> GetFiscalDataAsync(string customerId)
    {
        var f = await _ctx.FiscalClients.FindAsync(customerId);
        return f is null ? null : new FiscalClientDto(
            f.Id, f.Id, f.Rfc, f.RazonSocial, f.RegimenFiscal, f.CodigoPostal, f.UsoCfdi);
    }

    public async Task SaveFiscalDataAsync(FiscalClientDto data)
    {
        var existing = await _ctx.FiscalClients.FindAsync(data.Id);
        if (existing is not null)
        {
            existing.Rfc = data.Rfc;
            existing.RazonSocial = data.RazonSocial;
            existing.CodigoPostal = data.CodigoPostal;
            existing.RegimenFiscal = data.RegimenFiscal;
            existing.UsoCfdi = data.UsoCfdi;
        }
        else
        {
            _ctx.FiscalClients.Add(new FiscalClientEntity
            {
                Id = data.Id,
                Rfc = data.Rfc,
                RazonSocial = data.RazonSocial,
                CodigoPostal = data.CodigoPostal,
                RegimenFiscal = data.RegimenFiscal,
                UsoCfdi = data.UsoCfdi
            });
        }
        await _ctx.SaveChangesAsync();
    }
}
