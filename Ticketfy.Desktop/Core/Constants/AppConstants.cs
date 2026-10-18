namespace Ticketfy.Core.Constants;

/// <summary>
/// Application-wide constants migrated from src/constants.ts.
/// Product categories, seed data definitions, and system defaults.
/// </summary>
public static class AppConstants
{
    /// <summary>App Meta</summary>
    public const string AppName = "TICKETFY!";
    /// <summary>Default VAT / IVA rate (16%)</summary>
    public const double DefaultIvaRate = 0.16;


    /// <summary>Default database filename.</summary>
    public const string DatabaseFileName = "ticketfy.db";

    /// <summary>Audit log database filename (encrypted via SQLCipher if available).</summary>
    public const string AuditDatabaseFileName = "audit_logs.db";

    /// <summary>
    /// Product category list matching the legacy CATEGORIES array.
    /// "Todos" is a virtual filter (all categories), not persisted.
    /// </summary>
    public static readonly string[] Categories =
    [
        "Todos",
        "Abarrotes",
        "Bebidas",
        "Lácteos",
        "Botanas",
        "Farmacia",
        "Limpieza"
    ];

    /// <summary>Facturama sandbox API base URL for CFDI 4.0.</summary>
    public const string FacturamaSandboxUrl = "https://sandbox-api.facturama.mx/api/v3/cfdi";

    /// <summary>Facturama production API base URL.</summary>
    public const string FacturamaProductionUrl = "https://api.facturama.mx/api/v3/cfdi";

    /// <summary>Generic RFC for "Público en General" global invoices.</summary>
    public const string RfcPublicoGeneral = "XAXX010101000";

    /// <summary>Maximum file size for logo uploads (300 KB).</summary>
    public const int MaxLogoSizeBytes = 300 * 1024;

    /// <summary>ESC/POS initialization command.</summary>
    public static readonly byte[] EscPosInit = [0x1B, 0x40];

    /// <summary>ESC/POS paper cut command.</summary>
    public static readonly byte[] EscPosCut = [0x1D, 0x56, 0x00];

    /// <summary>ESC/POS cash drawer open pulse.</summary>
    public static readonly byte[] EscPosOpenDrawer = [0x1B, 0x70, 0x00, 0x19, 0xFA];
}
