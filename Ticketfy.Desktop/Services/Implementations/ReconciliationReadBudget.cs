using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Bound the legacy validator's materialized evidence before loading it. Never truncate a ledger.</summary>
internal sealed class ReconciliationReadBudget
{
    internal const string CloseBlocked = "READ_BUDGET: no se puede verificar el cierre por evidencia excesiva o texto no válido. El turno permanece abierto. Conserve los registros y solicite revisión técnica; no ajuste saldos.";
    internal async Task<bool> FitsShiftAsync(AppDbContext db, ShiftEntity shift) =>
        await ReserveAsync<SaleEntity>(db, "ShiftId = {0}", shift.Id, ["Id", "OperationId", "ShiftId", "RegisterId", "CashierUserId"]) &&
        await ReserveAsync<SalePaymentEntity>(db, "ShiftId = {0}", shift.Id, ["Id", "SaleId", "OperationId", "ShiftId", "RegisterId", "CashierUserId"]) &&
        await ReserveAsync<ShiftMovementEntity>(db, "shift_id = {0}", shift.Id) &&
        await ReserveAsync<PaymentRefundEntity>(db, "ShiftId = {0}", shift.Id) &&
        await ReserveAsync<CustomerPaymentEntity>(db, "shift_id = {0}", shift.Id) &&
        await ReserveAsync<AccountCreditSettlementEntity>(db, "ShiftId = {0}", shift.Id) &&
        await ReserveAsync<SupplierPaymentEntity>(db, "ShiftId = {0}", shift.Id) &&
        await ReserveAsync<ExpenseEntity>(db, "ShiftId = {0} OR id IN (SELECT entity_id FROM transaction_journal WHERE ShiftId = {0} AND event_type = 'EXPENSE_CANCELLED')", shift.Id) &&
        await ReserveAsync<AccountEntryEntity>(db, "ShiftId = {0} AND EntryType = 'CASH_LOAD'", shift.Id) &&
        await ReserveAsync<TransactionJournalEntryEntity>(db, "ShiftId = {0}", shift.Id) &&
        await ReserveAsync<CommandReceiptEntity>(db, "ShiftId = {0}", shift.Id) &&
        await ReserveAsync<CommandReceiptEntity>(db, "OperationId = {0}", shift.CloseOperationId ?? "") &&
        await ReserveAsync<CashupEntity>(db, "shift_id = {0}", shift.Id);

    private long _rows, _bytes;
    private sealed class Size
    {
        public long Rows { get; set; }
        public long Bytes { get; set; }
        public long Invalid { get; set; }
    }
    internal async Task<bool> ReserveAsync<T>(AppDbContext db, string predicate, object value, string[]? fields = null) where T : class
    {
        var entity = db.Model.FindEntityType(typeof(T))!;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        // Names come exclusively from the application's EF model and code-owned predicates.
        var columns = entity.GetProperties().Where(p => p.ClrType == typeof(string) && (fields is null || fields.Contains(p.Name)))
            .Select(p => "\"" + p.GetColumnName(table)!.Replace("\"", "\"\"") + "\"").ToArray();
        string Bytes(string c) => $"length(CAST(COALESCE({c}, '') AS BLOB))";
        var sql = $"SELECT COUNT(*) AS Rows, COALESCE(SUM({string.Join(" + ", columns.Select(Bytes))}),0) AS Bytes," +
            $" COALESCE(MAX(CASE WHEN {string.Join(" OR ", columns.Select(c => $"{Bytes(c)} > 8192 OR instr(COALESCE({c}, ''), char(0)) > 0"))} THEN 1 ELSE 0 END),0) AS Invalid" +
            $" FROM \"{table.Name}\" WHERE {predicate}";
        var size = await db.Database.SqlQuery<Size>(FormattableStringFactory.Create(sql, value)).SingleAsync();
        _rows = checked(_rows + size.Rows); _bytes = checked(_bytes + size.Bytes);
        return size.Invalid == 0 && _rows <= 50000 && _bytes <= 16 * 1024 * 1024;
    }
}
