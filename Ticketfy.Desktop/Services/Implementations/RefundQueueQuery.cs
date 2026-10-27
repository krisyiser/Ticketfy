using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Implementations;

internal static class RefundQueueQuery
{
    public static string Validate(int size, string? search)
    {
        if (size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(size));
        if (search?.Any(char.IsControl) == true) throw new ArgumentException("La búsqueda no admite caracteres de control.", nameof(search));
        search = search?.Trim() ?? "";
        if (search.Length > 128) throw new ArgumentException("La búsqueda admite hasta 128 caracteres.", nameof(search));
        return search;
    }

    public static void ValidateCursor(RefundQueueCursor? cursor, bool credit, OperationIdentity actor, string search)
    {
        if (cursor is not null && (cursor.AccountCredit != credit || cursor.RegisterId != actor.RegisterId ||
            cursor.UserId != actor.UserId || cursor.Search != search || string.IsNullOrEmpty(cursor.AfterId)))
            throw new ArgumentException("La posición pertenece a otra consulta, caja o usuario. Vuelva a buscar.", nameof(cursor));
    }
}
