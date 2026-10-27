namespace Ticketfy.Data.Dtos;

public sealed record KitDirectoryRow(string Id, string ProductId, string Barcode, string Name, decimal Price,
    bool IsActive, long ComponentCount, bool HasCatalogMismatch)
{
    public string Status => HasCatalogMismatch ? "Revisar vínculo con catálogo" : IsActive ? "Activo" : "Inactivo";
}

public sealed record KitComponentPage(KitDirectoryRow Kit, string Description, List<ItemKitItemDto> Items,
    bool HasMore);

public sealed record WalletDirectoryRow(string Id, string CardNumber, decimal RecordedBalance,
    string? CustomerId, bool IsActive)
{
    public string Status => IsActive ? "Activo" : "Inactivo";
}

public sealed record WalletDirectoryPage(List<WalletDirectoryRow> Items, bool HasMore, long TotalCount,
    decimal TotalRecordedBalance);
