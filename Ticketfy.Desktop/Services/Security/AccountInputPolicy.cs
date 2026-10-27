using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Security;

/// <summary>Write-time validation is separate from legacy credential verification. Secrets are always literal input.</summary>
internal static class AccountInputPolicy
{
    internal const int UsernameLimit = 128;
    internal const int PasswordLimit = 1024;
    internal const int PasswordMinimumCodePoints = 15;
    private const string IdentityCollation = "TICKETFY_USERNAME_V1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Username(string? value) => ProfileText(value, UsernameLimit, "Usuario");
    internal static string FullName(string? value) => ProfileText(value, 256, "Nombre");
    private static string ProfileText(string? value, int limit, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit
            || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new InvalidOperationException($"{field}: use entre 1 y {limit} caracteres, sin controles ni texto invisible.");
        try { return value.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new InvalidOperationException($"{field}: el texto Unicode no es válido."); }
    }

    internal static string PasswordHash(string? secret, string? username = null)
    {
        if (PasswordPolicy.RejectionReason(secret, username) is { } reason) throw new InvalidOperationException(reason);
        return SecurityManager.HashPassword(secret!);
    }

    internal static bool IsWellFormed(string value)
    {
        try { StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static string PinHash(string? pin) => string.IsNullOrEmpty(pin) ? "" : SecurityManager.HashPin(pin);

    internal static IQueryable<UserEntity> Named(AppDbContext db, string username)
    {
        // Query-only collation: no schema/index dependency, and backup readers need no custom SQLite extensions.
        ((SqliteConnection)db.Database.GetDbConnection()).CreateCollation(IdentityCollation,
            (left, right) => StringComparer.OrdinalIgnoreCase.Compare(CanonicalName(left), CanonicalName(right)));
        string canonical = CanonicalName(username);
        return db.Users.Where(u => EF.Functions.Collate(u.Username, IdentityCollation) == canonical);
    }

    private static string CanonicalName(string value)
    {
        try { return value.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return value; } // Invalid legacy text is not silently rewritten.
    }
}
