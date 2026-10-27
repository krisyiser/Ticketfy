using System.Text;

namespace Ticketfy.Services.Security;

/// <summary>Versioned, offline screening of whole secrets. Never transforms the secret used for hashing.</summary>
internal static class PasswordPolicy
{
    internal const int CurrentVersion = PasswordBlocklist.Version;
    internal const int ChangeRequired = -1;
    // These are public, predictable examples, not operator credentials or a complete breach corpus.
    private static readonly string[] CommonBases =
    [
        "password", "contraseña", "contrasena", "administrador", "administrator", "admin",
        "ticketfy", "nextvent", "puntodeventa", "punto de venta", "bienvenido", "bienvenida",
        "welcome", "letmein", "changeme", "cambiame", "temporal", "temporary", "qwerty",
        "iloveyou", "tequiero", "supervisor", "gerente", "cajero", "123456", "123456789"
    ];
    private static readonly string[] Suffixes =
        ["", "!", "!!", "123", "123!", "1234", "12345", "123456", "12345678", "123456789", "1234567890", "2025", "2026", "2027", "2026!"];
    private static readonly string[] Sequences =
        ["1234567890", "0123456789", "9876543210", "qwertyuiop", "asdfghjkl", "zxcvbnm", "abcdefghijklmnopqrstuvwxyz"];
    private static readonly HashSet<string> PublicExamples = new(StringComparer.OrdinalIgnoreCase)
    {
        "correcthorsebatterystaple", "correct horse battery staple", "thisisaverysecurepassword",
        "this is a secure password", "thisismypassword", "this is my password", "mysecretpassword",
        "qwertyuiopasdfghjkl", "qwertyuiopasdfgh", "qwertyuiopasdfghjklzxcvbnm"
    };

    internal static string? RejectionReason(string? secret, string? username = null)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > AccountInputPolicy.PasswordLimit)
            return $"Use una frase de al menos {AccountInputPolicy.PasswordMinimumCodePoints} caracteres Unicode y hasta {AccountInputPolicy.PasswordLimit} unidades de texto.";
        if (!AccountInputPolicy.IsWellFormed(secret)) return "La contraseña contiene texto Unicode no válido.";
        if (secret.EnumerateRunes().Count() < AccountInputPolicy.PasswordMinimumCodePoints)
            return $"Use una frase de al menos {AccountInputPolicy.PasswordMinimumCodePoints} caracteres; puede incluir espacios.";
        // Screening ignores predictable edge padding; hashing still receives the exact original text.
        string candidate = secret.Trim().Normalize(NormalizationForm.FormC);
        if (username is not null && !AccountInputPolicy.IsWellFormed(username))
            return "La identidad de la cuenta requiere revisión antes de validar su contraseña.";
        bool singleRepeatedCharacter = candidate.EnumerateRunes().Distinct().Take(2).Count() == 1;
        bool blocked = singleRepeatedCharacter || PublicExamples.Contains(candidate) || PasswordBlocklist.Current.Contains(candidate)
            || Sequences.Any(sequence => IsSequence(candidate, sequence))
            || CommonBases.Any(word => IsExpected(candidate, word))
            || !string.IsNullOrWhiteSpace(username) && IsExpected(candidate, username.Trim().Normalize(NormalizationForm.FormC));
        return blocked ? "La contraseña es común o previsible. Elija una frase distinta, no derivada del usuario ni de Ticketfy." : null;
    }

    internal static void Validate(string secret, string? username = null)
    {
        if (RejectionReason(secret, username) is { } reason) throw new InvalidOperationException(reason);
    }

    private static bool IsExpected(string candidate, string word) =>
        Suffixes.Any(suffix => string.Equals(candidate, word + suffix, StringComparison.OrdinalIgnoreCase))
        || string.Equals(candidate, word + word, StringComparison.OrdinalIgnoreCase)
        || string.Equals(candidate, word + word + word, StringComparison.OrdinalIgnoreCase);

    private static bool IsSequence(string candidate, string sequence)
    {
        for (int i = 0; i < candidate.Length; i++)
            if (char.ToUpperInvariant(candidate[i]) != char.ToUpperInvariant(sequence[i % sequence.Length])) return false;
        return true;
    }
}
