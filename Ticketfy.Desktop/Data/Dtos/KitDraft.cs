namespace Ticketfy.Data.Dtos;

/// <summary>Revision belongs to the POS product, not the legacy recipe identifier.</summary>
public sealed record KitDraft(string ProductId, string Barcode, string Name, decimal Price,
    string Description, IReadOnlyList<ItemKitItemDto> Items, long? Revision = null, bool IsActive = true);

public sealed record KitCapabilities(bool CanCreate, bool CanEdit, bool CanDeactivate);
