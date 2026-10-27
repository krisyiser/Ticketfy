using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;

namespace Ticketfy.Services.Implementations;

internal static class BankReferenceGuard
{
    // Call inside the write transaction, after checking an exact idempotent replay.
    public static async Task RequireUnusedAsync(AppDbContext db, string reference)
    {
        string normalized = reference.Trim().ToUpperInvariant();
        if (await db.AccountCreditSettlements.AnyAsync(s => s.ExternalReference != null && s.ExternalReference.ToUpper() == normalized)
            || await db.RefundSettlements.AnyAsync(s => s.ExternalReference.ToUpper() == normalized)
            || await db.IncomingTransferReceipts.AnyAsync(s => s.ExternalReference.ToUpper() == normalized || (s.RefundReference != null && s.RefundReference.ToUpper() == normalized))
            || await db.TenderConfirmations.AnyAsync(s => s.ExternalReference.ToUpper() == normalized)
            || await db.SalePayments.AnyAsync(s => s.ExternalReference != null && s.ExternalReference.ToUpper() == normalized)
            || await db.SupplierPayments.AnyAsync(s => s.Reference != null && s.Reference.ToUpper() == normalized))
            throw new InvalidOperationException("La referencia bancaria ya respalda otra operación. Revise el comprobante.");
    }
}
