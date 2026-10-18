using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Ticketfy.Core.Configuration;
using System.Text.Json.Nodes;

namespace Ticketfy.Services.Security;

/// <summary>
/// Enterprise security manager for PBKDF2 salted password hashing and dynamic master key management.
/// Conforms to Valcore Desktop Protocol v4.0 security directives.
/// </summary>
public static class SecurityManager
{
    private const int SaltByteSize = 16;
    private const int HashByteSize = 32;
    internal const int Pbkdf2Iterations = 600000;
    private const string FormatPrefix = "$pbkdf2$sha256$";

    /// <summary>
    /// Computes a PBKDF2 HMAC-SHA256 password hash with a unique 128-bit random salt.
    /// </summary>
    public static string HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;

        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            HashByteSize);

        string saltB64 = Convert.ToBase64String(salt);
        string hashB64 = Convert.ToBase64String(hash);

        return $"{FormatPrefix}{Pbkdf2Iterations}${saltB64}${hashB64}";
    }

    /// <summary>
    /// Verifies a candidate password against a PBKDF2 salted hash or legacy SHA256 string.
    /// </summary>
    public static bool VerifyPassword(string providedPassword, string storedHash)
    {
        if (string.IsNullOrEmpty(providedPassword) || string.IsNullOrEmpty(storedHash))
            return false;

        // 1. Verify every supported PBKDF2 format, including older iteration counts.
        if (storedHash.StartsWith(FormatPrefix, StringComparison.Ordinal))
        {
            try
            {
                string payload = storedHash.Substring(FormatPrefix.Length);
                string[] parts = payload.Split('$');
                if (parts.Length != 3 || !int.TryParse(parts[0], out int iterations)) return false;
                if (iterations < 10000 || iterations > 5000000) return false;

                byte[] salt = Convert.FromBase64String(parts[1]);
                byte[] expectedHash = Convert.FromBase64String(parts[2]);
                if (salt.Length is < 16 or > 64 || expectedHash.Length != HashByteSize) return false;

                byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(providedPassword),
                    salt,
                    iterations,
                    HashAlgorithmName.SHA256,
                    expectedHash.Length);

                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error parsing PBKDF2 hash payload during verification");
                return false;
            }
        }

        // 2. Legacy fallback for unsalted SHA256 (Base64 or Hex) for backwards compatibility
        return VerifyLegacySha256(providedPassword, storedHash);
    }

    public static bool NeedsRehash(string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || !storedHash.StartsWith(FormatPrefix, StringComparison.Ordinal))
            return true;

        string[] parts = storedHash.Substring(FormatPrefix.Length).Split('$');
        return parts.Length != 3 || !int.TryParse(parts[0], out int iterations) || iterations < Pbkdf2Iterations;
    }

    public static bool IsPasswordHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith(FormatPrefix, StringComparison.Ordinal))
        {
            string[] parts = value.Substring(FormatPrefix.Length).Split('$');
            if (parts.Length != 3 || !int.TryParse(parts[0], out int iterations) || iterations < 10000) return false;
            if (iterations > 5000000) return false;
            try
            {
                int saltLength = Convert.FromBase64String(parts[1]).Length;
                return saltLength is >= 16 and <= 64 && Convert.FromBase64String(parts[2]).Length == HashByteSize;
            }
            catch (FormatException) { return false; }
        }
        return IsLegacySha256Shape(value);
    }

    public static string HashPin(string pin)
    {
        ValidatePinShape(pin);
        return HashPassword(pin);
    }

    public static bool VerifyPin(string providedPin, string storedPin)
    {
        if (!IsValidPinShape(providedPin) || string.IsNullOrWhiteSpace(storedPin)) return false;
        if (storedPin.StartsWith(FormatPrefix, StringComparison.Ordinal) || IsLegacySha256Shape(storedPin))
            return VerifyPassword(providedPin, storedPin);

        // Transitional support only: successful callers must immediately persist a hash.
        if (!IsValidPinShape(storedPin)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedPin), Encoding.UTF8.GetBytes(storedPin));
    }

    public static bool PinNeedsRehash(string? storedPin) =>
        string.IsNullOrWhiteSpace(storedPin) || !storedPin.StartsWith(FormatPrefix, StringComparison.Ordinal) || NeedsRehash(storedPin);

    private static void ValidatePinShape(string pin)
    {
        if (!IsValidPinShape(pin)) throw new ArgumentException("El PIN debe contener exactamente cuatro dígitos.", nameof(pin));
    }

    private static bool IsValidPinShape(string value) => value.Length == 4 && value.All(char.IsAsciiDigit);

    private static bool IsLegacySha256Shape(string value)
    {
        if (value.Length == 64 && value.All(Uri.IsHexDigit)) return true;
        if (value.Length != 44 || !value.EndsWith('=')) return false;
        try { return Convert.FromBase64String(value).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool VerifyLegacySha256(string providedPassword, string storedHash)
    {
        try
        {
            using var sha256 = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(providedPassword);
            byte[] hash = sha256.ComputeHash(bytes);

            byte[] expected = storedHash.Length == 64
                ? Convert.FromHexString(storedHash)
                : Convert.FromBase64String(storedHash);
            return expected.Length == hash.Length && CryptographicOperations.FixedTimeEquals(hash, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Retrieves the database encryption key dynamically without hardcoded static credentials.
    /// </summary>
    public static string GetMasterKey()
    {
        // 1. Environment Variable Vault
        string? envKey = Environment.GetEnvironmentVariable("TICKETFY_DB_MASTER_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey;

        AppDataPaths.EnsureRequiredDirectories();
        var protectedStore = new DpapiSecretStore(AppDataPaths.MasterKeyFile);
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string fleetVaultPath = Path.Combine(userHome, ".fleet", "config.json");
        if (protectedStore.Exists)
        {
            string protectedKey = protectedStore.Read();
            RemoveLegacyPlaintextKey(fleetVaultPath);
            return protectedKey;
        }

        // One-time import from the legacy plaintext vault, followed by removal
        // of only Ticketfy's key while preserving the rest of that config file.
        if (File.Exists(fleetVaultPath))
        {
            try
            {
                string content = File.ReadAllText(fleetVaultPath);
                using var jsonDoc = System.Text.Json.JsonDocument.Parse(content);
                if (jsonDoc.RootElement.TryGetProperty("ticketfy_master_key", out var keyProp) && !string.IsNullOrWhiteSpace(keyProp.GetString()))
                {
                    string importedKey = keyProp.GetString()!;
                    protectedStore.Write(importedKey);
                    RemoveLegacyPlaintextKey(fleetVaultPath);
                    return importedKey;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("No se pudo importar la llave heredada de la base de datos.", ex);
            }
        }

        // Existing installations used this deterministic key. Import it once so
        // the database remains readable, then protect it with DPAPI.
        string key = File.Exists(AppDataPaths.DatabaseFile)
            ? DeriveLegacyMachineKey()
            : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        protectedStore.Write(key);
        return key;
    }

    internal static string GetOrCreateProtectedSecret(string path)
    {
        var store = new DpapiSecretStore(path);
        if (store.Exists) return store.Read();
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        store.Write(secret);
        return secret;
    }

    private static string DeriveLegacyMachineKey()
    {
        string machineSeed = $"{Environment.MachineName}-{Environment.UserName}-{Environment.ProcessorCount}";
        byte[] salt = Encoding.UTF8.GetBytes("TicketfyLocalMachineSalt2026!");
        byte[] derivedKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(machineSeed), salt, 10000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(derivedKey);
    }

    private static void RemoveLegacyPlaintextKey(string path)
    {
        if (!File.Exists(path)) return;
        JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
        if (node is not JsonObject obj || !obj.Remove("ticketfy_master_key")) return;
        string temporary = path + ".ticketfy.tmp";
        File.WriteAllText(temporary, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
