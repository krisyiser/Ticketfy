using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.Services.Implementations;

public sealed class GiftcardService : IGiftcardService
{
    private readonly AppDbContext _ctx;

    public GiftcardService(AppDbContext ctx) => _ctx = ctx;

    public async Task<List<GiftcardEntity>> GetAllAsync()
    {
        return await _ctx.Giftcards
            .AsNoTracking()
            .Where(g => g.IsActive)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
    }

    public async Task<GiftcardEntity?> GetByCardNumberAsync(string cardNumber)
    {
        return await _ctx.Giftcards
            .FirstOrDefaultAsync(g => g.CardNumber == cardNumber && g.IsActive);
    }

    public async Task<bool> DeductBalanceAsync(string cardNumber, decimal amount)
    {
        var card = await _ctx.Giftcards.FirstOrDefaultAsync(g => g.CardNumber == cardNumber && g.IsActive);
        if (card == null || card.Balance < amount) return false;

        card.Balance -= amount;
        await _ctx.SaveChangesAsync();
        return true;
    }

    public async Task<(bool IsValid, decimal Balance, string Error)> ValidateCardAsync(string cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber))
            return (false, 0m, "Ingrese un número de tarjeta válido.");

        var card = await _ctx.Giftcards.FirstOrDefaultAsync(g => g.CardNumber == cardNumber && g.IsActive);
        if (card == null)
            return (false, 0m, "Tarjeta / Monedero no existente o inactivo.");

        if (card.Balance <= 0)
            return (false, 0m, "Monedero sin fondos disponibles ($0.00).");

        return (true, card.Balance, string.Empty);
    }

    public async Task<bool> RedeemBalanceAsync(string cardNumber, decimal amountToRedeem, string saleId = "")
    {
        var card = await _ctx.Giftcards.FirstOrDefaultAsync(g => g.CardNumber == cardNumber && g.IsActive)
            ?? throw new InvalidOperationException("Monedero no válido.");

        if (card.Balance < amountToRedeem)
            throw new InvalidOperationException("Fondos insuficientes en el monedero.");

        card.Balance -= amountToRedeem;
        _ctx.Giftcards.Update(card);
        await _ctx.SaveChangesAsync();
        return true;
    }

    public async Task CreateCardAsync(string cardNumber, decimal initialBalance, string? customerId = null)
    {
        var entity = new GiftcardEntity
        {
            Id = Guid.NewGuid().ToString(),
            CardNumber = cardNumber,
            Balance = initialBalance,
            CustomerId = customerId,
            IsActive = true,
            CreatedAt = DateTime.Now.ToString("g")
        };
        _ctx.Giftcards.Add(entity);
        await _ctx.SaveChangesAsync();
    }

    public async Task RechargeAsync(string cardId, decimal amount, Ticketfy.Core.Enums.PaymentMethod method, string activeShiftId)
    {
        var card = await _ctx.Giftcards.FindAsync(cardId);
        if (card == null) return;

        card.Balance += amount;

        // GUARANTEE MONEY INFLOW RECORD
        if (method == Ticketfy.Core.Enums.PaymentMethod.Efectivo)
        {
            _ctx.ShiftMovements.Add(new ShiftMovementEntity
            {
                Id = Guid.NewGuid().ToString(),
                ShiftId = activeShiftId,
                MovementType = Ticketfy.Core.Enums.MovementType.AbonoCliente, // Treat as generic inflow
                Amount = amount,
                IsOutflow = false,
                Description = $"Recarga Monedero {card.CardNumber}",
                Timestamp = DateTime.Now.ToString("s")
            });
        }

        await _ctx.SaveChangesAsync();
    }
}
