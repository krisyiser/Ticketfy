using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal static class CommandIdentity
{
    public static string Hash(params object?[] values)
    {
        string Encode(object? value)
        {
            if (value is null) return "-1:";
            string text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString()!;
            return text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(values.Select(Encode)))));
    }

    public static async Task BindAsync(AppDbContext db, OperationIdentity actor, string operation, string command, params object?[] values)
    {
        string fingerprint = Hash([command, actor.UserId, actor.RegisterId, actor.ShiftId, .. values]);
        var previous = await db.CommandReceipts.AsNoTracking().SingleOrDefaultAsync(c => c.OperationId == operation);
        if (previous is not null)
        {
            if (previous.Fingerprint != fingerprint) throw new InvalidOperationException("La clave de operación ya pertenece a una solicitud diferente. No se ha realizado otro movimiento.");
            return;
        }
        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            OperationId = operation,
            Command = command,
            Fingerprint = fingerprint,
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId
        });
    }

    public static string Sale(SaleDto sale) => Hash(sale.CustomerId, sale.Total, sale.PaidAmount, sale.ChangeAmount,
        sale.PaymentMethod, sale.IsCredit, sale.GlobalDiscountAmount, sale.CashAmount, sale.CardAmount,
        sale.GiftcardAmount, sale.GiftcardNumber,
        string.Join("", sale.Items.Select(i => Hash(i.ProductId, i.Quantity, i.UnitPrice, i.OriginalUnitPrice,
            i.TotalPrice, i.AppliedDiscountAmount, i.AppliedPromotionId))),
        sale.Payments is null ? null : string.Join("", sale.Payments.OrderBy(p => p.Kind).Select(p => Hash(p.Kind, p.Amount, p.AccountId, p.Reference))));
}
