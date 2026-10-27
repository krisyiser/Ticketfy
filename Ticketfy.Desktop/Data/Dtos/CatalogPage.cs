namespace Ticketfy.Data.Dtos;

public sealed record CatalogPage<T>(List<T> Items, bool HasMore);
