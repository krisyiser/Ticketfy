using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Ticketfy.Services.Security;

/// <summary>Validates Mercado Pago's x-signature manifest using HMAC-SHA256.</summary>
public static class MercadoPagoWebhookVerifier
{
    public static bool IsValid(
        string xSignature,
        string xRequestId,
        string dataId,
        string secret,
        DateTimeOffset now,
        TimeSpan? allowedClockSkew = null)
    {
        if (string.IsNullOrWhiteSpace(xSignature) || string.IsNullOrWhiteSpace(xRequestId) ||
            string.IsNullOrWhiteSpace(dataId) || string.IsNullOrWhiteSpace(secret)) return false;

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in xSignature.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = item.IndexOf('=');
            if (separator > 0) parts[item[..separator]] = item[(separator + 1)..];
        }
        if (!parts.TryGetValue("ts", out string? timestamp) || !parts.TryGetValue("v1", out string? signature) ||
            !long.TryParse(timestamp, out long unixSeconds)) return false;

        TimeSpan skew = allowedClockSkew ?? TimeSpan.FromMinutes(5);
        DateTimeOffset signedAt;
        try
        {
            signedAt = unixSeconds > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixSeconds)
                : DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException) { return false; }
        if ((now - signedAt).Duration() > skew) return false;

        string manifest = $"id:{dataId.ToLowerInvariant()};request-id:{xRequestId};ts:{timestamp};";
        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature); }
        catch (FormatException) { return false; }
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}
