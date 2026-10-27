using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Implementations;

public partial class ExpenseService
{
    // Keep the selection shared by the financial summary and the receipt history.
    // A legacy cancellation has no receiving-shift evidence; do not assign the expense's original shift.
    private sealed class ReversalScalar
    {
        public string? Date { get; set; }
        public decimal Amount { get; set; }
        public string PaymentMethod { get; set; } = "";
    }

    private static IQueryable<ReversalScalar> ReversalScalars(AppDbContext db, string? shiftId)
    {
        var journal = from j in db.TransactionJournal.AsNoTracking()
                      join e in db.Expenses.AsNoTracking() on j.EntityId equals e.Id
                      where j.EventType == "EXPENSE_CANCELLED" && e.IsCancelled && (shiftId == null || j.ShiftId == shiftId)
                      select new ReversalScalar { Date = j.CreatedAtUtc, Amount = j.Amount, PaymentMethod = e.PaymentMethod };
        var legacy = db.Expenses.AsNoTracking().Where(e => shiftId == null && e.IsCancelled
            && !db.TransactionJournal.Any(j => j.EntityId == e.Id && j.EventType == "EXPENSE_CANCELLED"))
            .Select(e => new ReversalScalar { Date = e.CancelledAtUtc, Amount = e.Amount, PaymentMethod = e.PaymentMethod });
        return journal.Concat(legacy);
    }

    private sealed class ReversalKey
    {
        public string Id { get; set; } = "";
        public string ExpenseId { get; set; } = "";
        public int Legacy { get; set; }
    }

    private sealed record CancellationRows(List<ExpenseCancellationDto> Items, int Count, int Unknown, decimal Amount);

    private static async Task<CancellationRows> ReadCancellationsAsync(AppDbContext db, BusinessDateQuery period,
        string? shiftId, int offset, int size, bool includeUndated)
    {
        int count = 0, unknown = 0; decimal amount = 0m;
        await foreach (var reversal in ReversalScalars(db, shiftId).AsAsyncEnumerable())
        {
            if (!BusinessTime.TryLocal(reversal.Date, out var date, period.Zone))
            { unknown = checked(unknown + 1); if (includeUndated) count = checked(count + 1); }
            else if (date >= period.Start && date <= period.End)
            { count = checked(count + 1); amount += reversal.Amount; }
        }
        period.RegisterOn(db);
        // Page identifiers first, then materialize only those documents and journal entries.
        // Both branches mirror ReversalScalars, including the absence of a legacy receiving shift.
        var keys = await db.Database.SqlQuery<ReversalKey>($"""
            SELECT Id, ExpenseId, Legacy FROM (
                SELECT j.id AS Id, e.Id AS ExpenseId, 0 AS Legacy, j.created_at_utc AS EventDate
                FROM transaction_journal j JOIN Expenses e ON j.entity_id = e.Id
                WHERE j.event_type = 'EXPENSE_CANCELLED' AND e.IsCancelled = 1
                  AND ({shiftId} IS NULL OR j.ShiftId = {shiftId})
                UNION ALL
                SELECT e.Id AS Id, e.Id AS ExpenseId, 1 AS Legacy, e.CancelledAtUtc AS EventDate
                FROM Expenses e
                WHERE {shiftId} IS NULL AND e.IsCancelled = 1
                  AND NOT EXISTS (SELECT 1 FROM transaction_journal j WHERE j.entity_id = e.Id AND j.event_type = 'EXPENSE_CANCELLED')
            )
            WHERE ({includeUndated} AND ticketfy_business_ticks(EventDate) IS NULL)
               OR (ticketfy_business_ticks(EventDate) >= {period.Start.Ticks}
                   AND ticketfy_business_ticks(EventDate) <= {period.End.Ticks})
            ORDER BY ticketfy_business_ticks(EventDate) DESC, Legacy, Id COLLATE BINARY
            LIMIT {size} OFFSET {offset}
            """).ToListAsync();
        if (keys.Count == 0) return new([], count, unknown, amount);
        var expenseIds = keys.Select(k => k.ExpenseId).Distinct().ToArray();
        var journalIds = keys.Where(k => k.Legacy == 0).Select(k => k.Id).ToArray();
        var expenses = await db.Expenses.AsNoTracking().Where(e => expenseIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
        var journal = await db.TransactionJournal.AsNoTracking().Where(j => journalIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id);
        var items = keys.Select(key =>
        {
            var original = Map(expenses[key.ExpenseId]);
            if (key.Legacy != 0)
                return new ExpenseCancellationDto(key.Id, null, original.CancelledAtUtc ?? "", original.Amount,
                    original.CancellationReason, null, null, null, true, original);
            var receipt = journal[key.Id];
            return new ExpenseCancellationDto(receipt.Id, receipt.OperationId, receipt.CreatedAtUtc, receipt.Amount,
                receipt.Details, receipt.CashierUserId, receipt.RegisterId, receipt.ShiftId, false, original);
        }).ToList();
        return new(items, count, unknown, amount);
    }
}
