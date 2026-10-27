using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using Ticketfy.Core.Operations;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>Read-only document budgets. The caller holds a snapshot or writer transaction.</summary>
internal static class PurchaseDocumentReader
{
    internal const int HeaderFieldBytes = 8192;
    internal const int PageLines = 10000;
    internal const int PageTextBytes = 8 * 1024 * 1024;
    internal const string Unavailable = "Desglose no verificable o fuera del límite de lectura. Conserve el documento y solicite revisión; no imprima ni devuelva mercancía con este detalle.";
    internal const string Deferred = "Desglose pendiente de consulta individual por el volumen de esta página. Abra Ver detalle.";

    internal static void RequireKey(string key, string parameter)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > CommercialDocumentLimits.IdentifierCharacters || key.Any(char.IsControl))
            throw new ArgumentException("La referencia requiere entre 1 y 128 caracteres sin controles.", parameter);
    }

    // Only application-owned column names enter SQL. Filters/IDs remain parameterized by EF.
    private static readonly string[] TextColumns =
        ["Id", "OperationId", "ShiftId", "RegisterId", "CashierUserId", "DueDate", "SupplierId", "SupplierName", "InvoiceNumber", "Date", "Notes"];
    private static string Bytes(string alias, string column) => $"length(CAST(COALESCE({alias}.\"{column}\", '') AS BLOB))";
    internal static string InvalidText(string expression, int maximum = HeaderFieldBytes, bool multiline = false) =>
        $"(length(CAST(COALESCE({expression},'') AS BLOB)) > {maximum} OR instr(COALESCE({expression},''),char(0)) > 0 OR " +
        $"COALESCE({expression},'') GLOB ('*[' || " + (multiline ? "char(1)||'-'||char(8)||char(11)||char(12)||char(14)||'-'||char(31)" : "char(1)||'-'||char(31)") +
        $" || char(127)||'-'||char(159) || ']*'))";
    private static readonly string HeaderSql = "SELECT " + string.Join(", ", TextColumns.Select(c =>
        $"CASE WHEN {Bytes("p", c)} <= {HeaderFieldBytes} AND NOT {InvalidText($"p.\"{c}\"", multiline: c == "Notes")} THEN p.\"{c}\" ELSE '' END AS \"{c}\"")) +
        ", p.\"LedgerVersion\", p.\"TotalCost\" FROM \"Purchases\" p";
    private static readonly string FootprintSql = """
        SELECT p."Id",
            (SELECT COUNT(*) FROM "PurchaseItems" i WHERE i."PurchaseId" = p."Id") AS "LineCount",
            (SELECT COALESCE(SUM(length(CAST(i."Id" AS BLOB)) + length(CAST(i."PurchaseId" AS BLOB)) +
                length(CAST(i."ProductId" AS BLOB)) + length(CAST(i."ProductName" AS BLOB))), 0)
             FROM "PurchaseItems" i WHERE i."PurchaseId" = p."Id") AS "ItemBytes",
        """ + string.Join(" + ", TextColumns.Select(c => Bytes("p", c))) + " AS \"HeaderBytes\", (" +
        string.Join(" OR ", TextColumns.Select(c => InvalidText($"p.\"{c}\"", multiline: c == "Notes"))) +
        " OR EXISTS(SELECT 1 FROM PurchaseItems i WHERE i.PurchaseId=p.Id AND (" +
        string.Join(" OR ", new[] { "Id", "PurchaseId", "ProductId", "ProductName" }.Select(c => InvalidText($"i.\"{c}\""))) +
        "))) AS \"HeaderTooLarge\" FROM \"Purchases\" p";

    private sealed class Footprint
    {
        public string Id { get; set; } = "";
        public long LineCount { get; set; }
        public long ItemBytes { get; set; }
        public long HeaderBytes { get; set; }
        public bool HeaderTooLarge { get; set; }
    }

    internal static Task<List<PurchaseEntity>> PageHeadersAsync(AppDbContext db, int offset, int size, string? search = null)
    {
        string exact = search?.Trim() ?? "", term = exact.ToUpperInvariant();
        // Select row keys before the CASE projection. Filtering the projected Id would
        // turn an indexed lookup into a full scan; all actual values are SQL parameters.
        return db.Purchases.FromSql(FormattableStringFactory.Create(HeaderSql + """
             WHERE p.rowid IN (
                SELECT rowid FROM "Purchases"
                WHERE ({2} = '' OR instr(upper("InvoiceNumber"), {2}) > 0 OR instr(upper("SupplierName"), {2}) > 0 OR "Id" = {3})
                ORDER BY "Date" DESC, "Id" LIMIT {0} OFFSET {1})
             ORDER BY p."Date" DESC, p."Id"
            """, size, offset, term, exact)).AsNoTracking().ToListAsync();
    }

    internal static Task<PurchaseEntity?> HeaderByIdAsync(AppDbContext db, string id) =>
        db.Purchases.FromSql(FormattableStringFactory.Create(HeaderSql + """ WHERE p."Id" = {0}""", id)).AsNoTracking().SingleOrDefaultAsync();

    internal static Task<PurchaseEntity?> HeaderByOperationAsync(AppDbContext db, string operation) =>
        db.Purchases.FromSql(FormattableStringFactory.Create(HeaderSql + """ WHERE p."OperationId" = {0}""", operation)).AsNoTracking().SingleOrDefaultAsync();

    internal static Task<PurchaseEntity?> HeaderForLineAsync(AppDbContext db, string line) =>
        db.Purchases.FromSql(FormattableStringFactory.Create(HeaderSql + """ WHERE p."Id" = (SELECT "PurchaseId" FROM "PurchaseItems" WHERE "Id" = {0})""",
            line)).AsNoTracking().SingleOrDefaultAsync();

    internal static async Task<List<PurchaseDto>> ReadAsync(AppDbContext db, List<PurchaseEntity> purchases)
    {
        if (purchases.Count == 0) return [];
        var ids = purchases.Select(p => p.Id).Distinct().ToArray();
        var footprints = await db.Database.SqlQueryRaw<Footprint>(FootprintSql).Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var accepted = new List<string>();
        var warnings = new Dictionary<string, string>();
        long lines = 0, bytes = 0;
        foreach (var purchase in purchases)
        {
            if (!footprints.TryGetValue(purchase.Id, out var size) || size.HeaderTooLarge ||
                size.LineCount is < 1 or > CommercialDocumentLimits.Lines ||
                size.HeaderBytes + size.ItemBytes > CommercialDocumentLimits.TotalTextBytes)
            { warnings[purchase.Id] = Unavailable; continue; }
            if (lines + size.LineCount > PageLines || bytes + size.HeaderBytes + size.ItemBytes > PageTextBytes)
            { warnings[purchase.Id] = Deferred; continue; }
            lines += size.LineCount; bytes += size.HeaderBytes + size.ItemBytes;
            accepted.Add(purchase.Id);
        }
        var items = accepted.Count == 0 ? Array.Empty<PurchaseItemEntity>().ToLookup(i => i.PurchaseId)
            : (await db.PurchaseItems.AsNoTracking().Where(i => accepted.Contains(i.PurchaseId))
                .OrderBy(i => i.Id).ToListAsync()).ToLookup(i => i.PurchaseId);
        var result = new List<PurchaseDto>();
        foreach (var purchase in purchases)
        {
            if (warnings.TryGetValue(purchase.Id, out var warning))
            { result.Add(Incomplete(purchase, warning)); continue; }
            var document = Map(purchase, items[purchase.Id]);
            try
            {
                ValidateDetail(document);
                result.Add(document);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
            { result.Add(Incomplete(purchase, Unavailable)); }
        }
        return await PurchaseFinancialReader.EnrichAsync(db, result);
    }

    internal static void ValidateDetail(PurchaseDto document)
    {
        if (!document.DetailsAvailable) throw new InvalidOperationException(Unavailable);
        CommercialDocumentLimits.ValidatePurchase(document);
        try
        {
            RequireKey(document.Id, nameof(document.Id)); RequireKey(document.SupplierId, nameof(document.SupplierId));
            foreach (var item in document.Items)
            {
                RequireKey(item.Id, nameof(item.Id)); RequireKey(item.ProductId, nameof(item.ProductId));
                if (item.PurchaseId != document.Id || item.ProductName.Any(char.IsControl)) throw new InvalidOperationException(Unavailable);
            }
        }
        catch (ArgumentException) { throw new InvalidOperationException(Unavailable); }
        if (document.Items.Select(i => i.Id).Distinct().Count() != document.Items.Count ||
            new[] { document.SupplierName, document.InvoiceNumber, document.Date }.Any(s => s.Any(char.IsControl)))
            throw new InvalidOperationException(Unavailable);
        if (document.TotalCost < 0 || document.Items.Any(i => !double.IsFinite(i.Quantity) || i.Quantity <= 0 ||
            Math.Round(i.Quantity, 3) != i.Quantity || i.UnitPrice <= 0 || CommercialLedger.Money(i.UnitPrice) != i.UnitPrice ||
            i.TotalPrice != CommercialLedger.Money(i.UnitPrice * (decimal)i.Quantity)) ||
            document.Items.Sum(i => i.TotalPrice) != document.TotalCost)
            throw new InvalidOperationException(Unavailable);
    }

    private static PurchaseDto Incomplete(PurchaseEntity p, string reason) =>
        Map(p, []) with { DetailsAvailable = false, DetailWarning = reason };

    internal static PurchaseDto Map(PurchaseEntity p, IEnumerable<PurchaseItemEntity> items) => new(p.Id, p.SupplierId,
        p.SupplierName, p.InvoiceNumber, p.Date, p.TotalCost, p.Notes,
        items.Select(i => new PurchaseItemDto(i.Id, i.PurchaseId, i.ProductId, i.ProductName, i.UnitPrice, i.Quantity, i.TotalPrice)).ToList(),
        p.OperationId, DueDate: p.DueDate)
    { IsHistoricalDocument = p.LedgerVersion == 0 };
}
