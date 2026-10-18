using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ticketfy.Logging;

public static partial class LogSanitizer
{
    [GeneratedRegex(@"(?i)(password|passwd|pwd|token|api[_-]?key|authorization)\s*[:=]\s*([^\s;,]+)")]
    private static partial Regex SecretAssignment();
    [GeneratedRegex(@"\b[A-Za-z0-9-_]{20,}\.[A-Za-z0-9-_]{20,}\.[A-Za-z0-9-_]{20,}\b")]
    private static partial Regex Jwt();
    [GeneratedRegex(@"\b[A-ZÑ&]{3,4}\d{6}[A-Z0-9]{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Rfc();
    [GeneratedRegex(@"\b[A-Z][AEIOU][A-Z]{2}\d{6}[HM][A-Z]{5}[A-Z0-9]\d\b", RegexOptions.IgnoreCase)]
    private static partial Regex Curp();
    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){13,19}(?!\d)")]
    private static partial Regex Pan();
    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string result = SecretAssignment().Replace(value, "$1=[REDACTED]");
        result = Jwt().Replace(result, "[JWT-REDACTED]");
        result = Curp().Replace(result, "[CURP-REDACTED]");
        result = Rfc().Replace(result, "[RFC-REDACTED]");
        result = Email().Replace(result, "[EMAIL-REDACTED]");
        result = Pan().Replace(result, match => PassesLuhn(match.Value) ? "[PAN-REDACTED]" : match.Value);
        return result;
    }

    public static string Pseudonymize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "anonymous";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())))[..12];
    }

    private static bool PassesLuhn(string raw)
    {
        string digits = new(raw.Where(char.IsDigit).ToArray());
        if (digits.Length is < 13 or > 19) return false;
        int sum = 0; bool alternate = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alternate && (n *= 2) > 9) n -= 9;
            sum += n; alternate = !alternate;
        }
        return sum % 10 == 0;
    }
}
