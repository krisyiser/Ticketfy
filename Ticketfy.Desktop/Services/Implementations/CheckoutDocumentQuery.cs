using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Operations;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal static class CheckoutDocumentQuery
{
    // Byte lengths are checked in SQLite, including text containing NUL. Oversize payloads
    // are NOT fetched. Empty read sentinels fail validation; the source columns stay intact.
    // Tracked callers may change state, never assign these sentinels back to payload columns.
    public static IQueryable<CheckoutOperationEntity> Bounded(AppDbContext db) =>
        db.CheckoutOperations.FromSqlInterpolated($"""
            SELECT operation_id, sale_id, state, payment_method, amount, payment_reference,
                   "ShiftId", "RegisterId", "CashierUserId", "FiscalIdempotencyKey",
                   failure_reason, created_at_utc, updated_at_utc,
                   CASE WHEN length(CAST(sale_payload AS BLOB)) <= {CommercialDocumentLimits.PayloadBytes}
                        THEN sale_payload ELSE '' END AS sale_payload,
                   CASE WHEN "FiscalPayload" IS NULL OR length(CAST("FiscalPayload" AS BLOB)) <= {CommercialDocumentLimits.PayloadBytes}
                        THEN "FiscalPayload" ELSE '' END AS "FiscalPayload"
            FROM checkout_operations
            """);
}
