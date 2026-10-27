namespace Ticketfy.Services.Implementations;

/// <summary>Fail explicitly when a legacy non-paged contract cannot return a complete bounded result.</summary>
internal static class LegacyCatalogReadPolicy
{
    internal const int Limit = 200;

    internal static void RequireComplete(bool hasMore)
    {
        if (hasMore)
            throw new InvalidOperationException($"La consulta supera {Limit} registros. Utilice la consulta paginada; no se devolvió una lista incompleta.");
    }
}
