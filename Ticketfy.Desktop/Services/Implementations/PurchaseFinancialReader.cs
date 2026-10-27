using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Scalar, read-only evidence. Caller owns an authorized snapshot or writer transaction.</summary>
internal static class PurchaseFinancialReader
{
    internal const string Unavailable = "Estado financiero no verificable. Conserve el documento y concilie sus registros; no vuelva a pagar ni devolver para ajustar el saldo.";
    private static string Key(string c, int maximum = 128) => $"({c} IS NOT NULL AND trim({c}) <> '' AND length({c}) <= {maximum} AND NOT {PurchaseDocumentReader.InvalidText(c, maximum * 4)})";
    private static string Row(string from, string doc, string op, string account, string type, string amount,
        string user, string register, string shift, string date, string method, string reference, string valid,
        string journal, string entity, string journalAmount) =>
        $"SELECT {doc} Doc, {op} Op, {account} Account, '{type}' Type, {amount} Amount, {user} User, {register} Register, {shift} Shift," +
        $" {date} Date, {method} Method, {reference} Reference, ({valid}) Valid, '{journal}' Event, {entity} Entity, {journalAmount} JournalAmount FROM {from}";

    // All identifiers and SQL fragments are code-owned. User filters are always parameters.
    private static readonly string Sources = string.Join(" UNION ALL ", new[]
    {
        Row("Purchases p WHERE p.LedgerVersion = 1", "p.Id", "p.OperationId", "p.SupplierId", "PURCHASE", "p.TotalCost",
            "p.CashierUserId", "p.RegisterId", "p.ShiftId", "p.Date", "''", "''", "p.TotalCost >= 0 AND " + OriginalStock, "PURCHASE_RECEIVED", "p.Id", "p.TotalCost"),
        Row("supplier_payments t", "t.PurchaseId", "t.OperationId", "t.SupplierId", "SUPPLIER_PAYMENT", "-t.Amount",
            "t.CashierUserId", "t.RegisterId", "t.ShiftId", "t.CreatedAtUtc", "CASE t.Kind WHEN 0 THEN 'Efectivo' WHEN 1 THEN 'Tarjeta' WHEN 4 THEN 'Transferencia' ELSE 'Desconocido' END", "t.Reference",
            $"t.Amount > 0 AND t.Kind IN (0,1,4) AND NOT {PurchaseDocumentReader.InvalidText("t.Reference", 800)} AND (t.Kind=0 OR trim(COALESCE(t.Reference,'')) <> '') AND " +
            "((t.Kind=0 AND (SELECT COUNT(*) FROM shift_movements m WHERE m.OperationId=t.OperationId)=1 AND EXISTS(SELECT 1 FROM shift_movements m WHERE m.OperationId=t.OperationId AND m.reference_id=t.PurchaseId AND m.amount=t.Amount AND m.movement_type=4 AND m.is_outflow=1 AND m.shift_id=t.ShiftId AND m.RegisterId=t.RegisterId AND m.CashierUserId=t.CashierUserId)) OR (t.Kind<>0 AND NOT EXISTS(SELECT 1 FROM shift_movements m WHERE m.OperationId=t.OperationId)))",
            "SUPPLIER_PAYMENT", "t.PurchaseId", "t.Amount"),
        Row("supplier_returns t", "t.PurchaseId", "t.OperationId", "t.SupplierId", "SUPPLIER_RETURN", "-t.Amount",
            "t.CashierUserId", "t.RegisterId", "t.ShiftId", "t.CreatedAtUtc", "'Crédito por devolución'", "t.Id",
            "t.Quantity > 0 AND t.Amount >= 0 AND (SELECT COUNT(*) FROM inventory_movements m WHERE m.OperationId=t.OperationId)=1 AND EXISTS(SELECT 1 FROM PurchaseItems i WHERE i.Id=t.PurchaseItemId AND i.PurchaseId=t.PurchaseId AND EXISTS(SELECT 1 FROM inventory_movements m WHERE m.OperationId=t.OperationId AND m.DocumentId=t.Id AND m.ProductId=i.ProductId AND m.Reason='SUPPLIER_RETURN' AND m.Quantity=-t.Quantity AND m.UnitCost=i.UnitPrice AND m.StockAfter=m.StockBefore+m.Quantity AND m.StockAfter>=0 AND m.ShiftId=t.ShiftId AND m.RegisterId=t.RegisterId AND m.CashierUserId=t.CashierUserId))",
            "SUPPLIER_RETURN", "t.Id", "t.Amount"),
        CreditRow(false, false), CreditRow(true, false), CreditRow(false, true),
        Row("historical_balance_reconciliations h WHERE h.Scope=1", "h.TargetId", "h.OperationId", "h.AccountId", "HISTORICAL_BALANCE_ADJUSTMENT", "h.Adjustment",
            "h.CashierUserId", "h.RegisterId", "NULL", "h.CreatedAtUtc", "'Conciliación histórica'", "h.EvidenceReference",
            "h.ConfirmedBalance=h.LedgerBefore+h.Adjustment AND h.ConfirmedBalance>=0 AND trim(h.EvidenceReference)<>'' AND EXISTS(SELECT 1 FROM Purchases q WHERE q.Id=h.TargetId AND q.LedgerVersion=0 AND h.ConfirmedBalance<=q.TotalCost)", "HISTORICAL_BALANCE_RECONCILED", "h.TargetId", "h.Adjustment")
    });
    private static string CreditRow(bool target, bool refund) => Row(
        $"account_credit_settlements t WHERE t.AccountKind=1 AND t.Kind={(refund ? 1 : 0)}", target ? "t.TargetDocumentId" : "t.SourceDocumentId", "t.OperationId", "t.AccountId",
        refund ? "CREDIT_REFUNDED" : target ? "CREDIT_APPLIED_TO" : "CREDIT_APPLIED_FROM", target ? "-t.Amount" : "t.Amount",
        "t.CashierUserId", "t.RegisterId", "t.ShiftId", "t.CreatedAtUtc", refund ? "CASE t.TenderKind WHEN 0 THEN 'Efectivo recibido' WHEN 1 THEN 'Tarjeta' WHEN 4 THEN 'Transferencia' ELSE 'Desconocido' END" : "'Aplicación de crédito'", "t.ExternalReference",
        "t.Amount > 0 AND EXISTS(SELECT 1 FROM Purchases q WHERE q.Id=t.SourceDocumentId AND q.SupplierId=t.AccountId) AND " + (refund ? $"t.TargetDocumentId IS NULL AND t.TenderKind IN (0,1,4) AND NOT {PurchaseDocumentReader.InvalidText("t.ExternalReference", 800)} AND (t.TenderKind=0 OR trim(COALESCE(t.ExternalReference,''))<>'') AND ((t.TenderKind=0 AND (SELECT COUNT(*) FROM shift_movements m WHERE m.OperationId=t.OperationId)=1 AND EXISTS(SELECT 1 FROM shift_movements m WHERE m.OperationId=t.OperationId AND m.reference_id=t.OperationId AND m.amount=t.Amount AND m.movement_type=12 AND m.is_outflow=0 AND m.shift_id=t.ShiftId AND m.RegisterId=t.RegisterId AND m.CashierUserId=t.CashierUserId)) OR (t.TenderKind<>0 AND NOT EXISTS(SELECT 1 FROM shift_movements m WHERE m.OperationId=t.OperationId)))" :
            "t.TenderKind IS NULL AND t.TargetDocumentId IS NOT NULL AND t.TargetDocumentId<>t.SourceDocumentId AND EXISTS(SELECT 1 FROM Purchases q WHERE q.Id=t.TargetDocumentId AND q.SupplierId=t.AccountId) AND NOT EXISTS(SELECT 1 FROM shift_movements m WHERE m.OperationId=t.OperationId)"),
        refund ? "ACCOUNT_CREDIT_REFUNDED" : "ACCOUNT_CREDIT_APPLIED", "t.OperationId", "t.Amount");

    private static string Match(string a, string x) => $"{a}.AccountKind=1 AND {a}.AccountId={x}.Account AND {a}.DocumentId={x}.Doc AND {a}.OperationId={x}.Op AND {a}.EntryType={x}.Type AND {a}.Amount={x}.Amount AND {a}.CashierUserId={x}.User AND {a}.RegisterId={x}.Register AND {a}.ShiftId IS {x}.Shift";
    private const string OriginalStock = """
        EXISTS(SELECT 1 FROM PurchaseItems i WHERE i.PurchaseId=p.Id)
        AND NOT EXISTS(SELECT 1 FROM inventory_movements m WHERE m.DocumentId=p.Id AND m.Reason='PURCHASE' AND
            (m.OperationId<>p.OperationId OR m.Quantity<=0 OR m.StockAfter<>m.StockBefore+m.Quantity OR m.StockBefore<0 OR m.StockAfter<0 OR
             m.ShiftId IS NOT p.ShiftId OR m.RegisterId<>p.RegisterId OR m.CashierUserId<>p.CashierUserId OR
             NOT EXISTS(SELECT 1 FROM PurchaseItems i WHERE i.PurchaseId=p.Id AND i.ProductId=m.ProductId AND i.UnitPrice=m.UnitCost)))
        AND NOT EXISTS(SELECT 1 FROM PurchaseItems i WHERE i.PurchaseId=p.Id AND
            COALESCE((SELECT SUM(m.Quantity) FROM inventory_movements m WHERE m.DocumentId=p.Id AND m.Reason='PURCHASE' AND m.ProductId=i.ProductId AND m.UnitCost=i.UnitPrice),0)
            <> (SELECT SUM(CAST(ROUND(j.Quantity*1000) AS INTEGER)) FROM PurchaseItems j WHERE j.PurchaseId=p.Id AND j.ProductId=i.ProductId AND j.UnitPrice=i.UnitPrice))
        """;
    private static readonly string EvidenceSql = $"SELECT x.* FROM ({Sources}) x";
    private static string Amount(string condition) => $"CAST(COALESCE((SELECT SUM(a.Amount) FROM account_entries a WHERE a.AccountKind=1 AND a.DocumentId=p.Id AND ({condition})),0) AS TEXT)";
    private static readonly string SummarySql = $"""
        SELECT p.Id, p.LedgerVersion=0 Historical,
            {Amount("1=1")} Balance,
            {Amount("a.EntryType='SUPPLIER_PAYMENT'")} Payments,
            {Amount("a.EntryType='SUPPLIER_RETURN'")} Returns,
            {Amount("a.EntryType='CREDIT_APPLIED_TO'")} CreditApplied,
            {Amount("a.EntryType='CREDIT_APPLIED_FROM'")} CreditTransferred,
            {Amount("a.EntryType='CREDIT_REFUNDED'")} CreditRefunded,
            (p.LedgerVersion IN (0,1) AND {Key("p.Id")} AND {Key("p.SupplierId")} AND EXISTS(SELECT 1 FROM Suppliers s WHERE s.Id=p.SupplierId)
            AND (p.LedgerVersion=1 OR EXISTS(SELECT 1 FROM historical_balance_reconciliations h WHERE h.Scope=1 AND h.TargetId=p.Id AND h.AccountId=p.SupplierId AND h.ConfirmedBalance=h.LedgerBefore+h.Adjustment AND h.ConfirmedBalance>=0 AND h.ConfirmedBalance<=p.TotalCost AND trim(h.EvidenceReference)<>''))
            AND NOT EXISTS(SELECT 1 FROM account_entries a WHERE a.AccountKind=1 AND a.DocumentId=p.Id AND NOT EXISTS(SELECT 1 FROM ({Sources}) x WHERE {Match("a", "x")}))
            AND NOT EXISTS(SELECT 1 FROM ({Sources}) x WHERE x.Doc=p.Id AND
                (NOT COALESCE(x.Valid,0) OR x.Account<>p.SupplierId OR NOT {Key("x.Op", 132)} OR NOT {Key("x.User")} OR NOT {Key("x.Register")}
                OR {PurchaseDocumentReader.InvalidText("x.Date", 128)} OR datetime(x.Date) IS NULL OR {PurchaseDocumentReader.InvalidText("x.Reference", 800)}
                OR (x.Type<>'HISTORICAL_BALANCE_ADJUSTMENT' AND NOT EXISTS(SELECT 1 FROM shifts s WHERE s.id=x.Shift AND s.RegisterId=x.Register AND s.CashierUserId=x.User))
                OR (NOT (x.Type='HISTORICAL_BALANCE_ADJUSTMENT' AND x.Amount=0) AND NOT EXISTS(SELECT 1 FROM account_entries a WHERE {Match("a", "x")}))
                OR (SELECT COUNT(*) FROM transaction_journal j WHERE j.operation_id=x.Op AND j.event_type=x.Event)<>1
                OR NOT EXISTS(SELECT 1 FROM transaction_journal j WHERE j.operation_id=x.Op AND j.event_type=x.Event AND j.entity_id=x.Entity AND j.amount=x.JournalAmount AND j.CashierUserId=x.User AND j.RegisterId=x.Register AND (x.Type='HISTORICAL_BALANCE_ADJUSTMENT' OR j.ShiftId IS x.Shift))))) Valid
        FROM Purchases p
        """;
    private sealed class State
    {
        public string Id { get; set; } = "";
        public bool Historical { get; set; }
        public bool Valid { get; set; }
        public decimal Balance { get; set; }
        public decimal Payments { get; set; }
        public decimal Returns { get; set; }
        public decimal CreditApplied { get; set; }
        public decimal CreditTransferred { get; set; }
        public decimal CreditRefunded { get; set; }
    }
    internal static async Task<List<PurchaseDto>> EnrichAsync(AppDbContext db, List<PurchaseDto> documents)
    {
        var ids = documents.Where(d => d.DetailsAvailable || d.IsHistoricalDocument).Select(d => d.Id).ToArray();
        if (ids.Length == 0) return documents;
        var byId = documents.ToDictionary(d => d.Id);
        var lines = documents.SelectMany(d => d.Items).ToDictionary(i => i.Id);
        var states = await db.Database.SqlQueryRaw<State>(SummarySql).Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var tenderTotals = new Dictionary<string, (decimal Cash, decimal Card, decimal Transfer)>();
        await foreach (var p in db.SupplierPayments.AsNoTracking().Where(p => ids.Contains(p.PurchaseId)).Select(p => new { p.PurchaseId, p.Kind, p.Amount }).AsAsyncEnumerable())
        {
            var old = tenderTotals.GetValueOrDefault(p.PurchaseId);
            tenderTotals[p.PurchaseId] = (old.Cash + (p.Kind == TenderKind.Cash ? p.Amount : 0), old.Card + (p.Kind == TenderKind.Card ? p.Amount : 0), old.Transfer + (p.Kind == TenderKind.Transfer ? p.Amount : 0));
        }
        var reserved = new Dictionary<string, decimal>();
        await foreach (var r in AccountCreditSettlementService.PendingRequests(db).Where(r => r.AccountKind == AccountKind.Supplier && ids.Contains(r.SourceDocumentId))
            .Select(r => new { r.SourceDocumentId, r.Amount, r.AccountId }).AsAsyncEnumerable())
        {
            var doc = byId[r.SourceDocumentId];
            if (r.Amount <= 0 || r.AccountId != doc.SupplierId) states[doc.Id].Valid = false;
            reserved[doc.Id] = reserved.GetValueOrDefault(doc.Id) + r.Amount;
        }
        var returned = new Dictionary<string, (decimal Quantity, decimal Amount)>();
        await foreach (var r in (from r in db.SupplierReturns.AsNoTracking()
                                 join i in db.PurchaseItems.AsNoTracking() on r.PurchaseItemId equals i.Id
                                 where ids.Contains(i.PurchaseId)
                                 select new { i.PurchaseId, PurchaseItemId = i.Id, r.Quantity, r.Amount, Valid = r.PurchaseId == i.PurchaseId }).AsAsyncEnumerable())
        {
            var line = lines.GetValueOrDefault(r.PurchaseItemId);
            if (!r.Valid || line is null) { states[r.PurchaseId].Valid = false; continue; }
            var old = returned.GetValueOrDefault(line.Id);
            returned[line.Id] = (old.Quantity + r.Quantity, old.Amount + r.Amount);
        }
        return documents.Select(doc =>
        {
            if (!states.TryGetValue(doc.Id, out var s) || !s.Valid) return doc with { FinancialWarning = Unavailable };
            foreach (var line in doc.Items)
            {
                var r = returned.GetValueOrDefault(line.Id);
                if (r.Quantity < 0 || r.Quantity > (decimal)line.Quantity || r.Amount != CommercialLedger.Money(line.TotalPrice * r.Quantity / (decimal)line.Quantity))
                    return doc with { FinancialWarning = Unavailable };
            }
            decimal reserve = reserved.GetValueOrDefault(doc.Id), balance = s.Balance / 100m;
            if (reserve > Math.Max(0m, -balance)) return doc with { FinancialWarning = Unavailable };
            var tenders = tenderTotals.GetValueOrDefault(doc.Id);
            var summary = new PurchaseFinancialSummary(-s.Payments / 100m, -s.Returns / 100m, -s.CreditApplied / 100m,
                s.CreditTransferred / 100m, s.CreditRefunded / 100m, balance, reserve, tenders.Cash, tenders.Card, tenders.Transfer, s.Historical);
            return doc with
            {
                Financial = summary,
                FinancialWarning = "",
                PaidAmount = summary.Payments,
                PaymentMethod = summary.Methods.Length == 0 ? "Sin pagos documentados" : summary.Methods,
                PaymentReference = null,
                Items = doc.Items.Select(i => i with { ReturnedQuantity = returned.GetValueOrDefault(i.Id).Quantity }).ToList()
            };
        }).ToList();
    }

    internal static async Task<PurchaseFinancialPage> PageAsync(AppDbContext db, PurchaseDto document, int page, int size)
    {
        var entries = db.AccountEntries.AsNoTracking().Where(a => a.AccountKind == AccountKind.Supplier && a.DocumentId == document.Id);
        long count = await entries.LongCountAsync();
        // SQLite projects only bounded strings; malformed evidence stays visible as unverified.
        string Safe(string expression) => $"CASE WHEN NOT {PurchaseDocumentReader.InvalidText(expression, 800)} THEN COALESCE({expression},'') ELSE '[No verificable]' END";
        string sql = $$"""
            SELECT a.PostingSequence AS Sequence, {{Safe("a.OperationId")}} OperationId, {{Safe("a.EntryType")}} EntryType,
                CAST(a.Amount AS TEXT) Amount, {{Safe("a.CreatedAtUtc")}} Date,
                COALESCE((SELECT {{Safe("x.Method")}} FROM ({{EvidenceSql}}) x WHERE {{Match("a", "x")}} LIMIT 1),'Sin evidencia') Method,
                COALESCE((SELECT {{Safe("x.Reference")}} FROM ({{EvidenceSql}}) x WHERE {{Match("a", "x")}} LIMIT 1),'') Reference,
                {{Safe("a.CashierUserId")}} CashierUserId, {{Safe("a.RegisterId")}} RegisterId, {{Safe("a.ShiftId")}} ShiftId
            FROM account_entries a WHERE a.AccountKind=1 AND a.DocumentId={0}
            ORDER BY a.PostingSequence LIMIT {1} OFFSET {2}
            """;
        var rows = await db.Database.SqlQuery<PurchaseFinancialEntryDto>(FormattableStringFactory.Create(sql, document.Id, size, checked(page * size))).ToListAsync();
        return new(rows.Select(r => r with { Amount = r.Amount / 100m }).ToList(), count, (long)page * size + rows.Count < count,
            document.Financial, document.FinancialWarning, document);
    }
}
