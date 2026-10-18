using System;
using System.Security.Cryptography;
using System.Text;
using Ticketfy.Services.Security;
using Ticketfy.Core.Services;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public class SecurityManagerTests
{
    [Fact]
    public void SessionManager_ShouldNotCreateAnImplicitAdministrator()
    {
        var sessionManager = new SessionManager();

        Assert.Null(sessionManager.CurrentCashier);
        Assert.False(sessionManager.IsTerminalLocked);
    }

    [Fact]
    public void HashPassword_ShouldGenerateUniqueSaltsForSamePassword()
    {
        string pass = "IndustrialPassword2026!";
        string hash1 = SecurityManager.HashPassword(pass);
        string hash2 = SecurityManager.HashPassword(pass);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.StartsWith("$pbkdf2$sha256$600000$", hash1);
        Assert.StartsWith("$pbkdf2$sha256$600000$", hash2);
        Assert.NotEqual(hash1, hash2); // Salts must be unique
    }

    [Fact]
    public void PasswordPolicy_ShouldFlagLegacyHashForProgressiveRehash()
    {
        string password = "LegacyIterationPassword!";
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100000, HashAlgorithmName.SHA256, 32);
        string encoded = $"$pbkdf2$sha256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

        Assert.True(SecurityManager.VerifyPassword(password, encoded));
        Assert.True(SecurityManager.NeedsRehash(encoded));
        Assert.False(SecurityManager.NeedsRehash(SecurityManager.HashPassword(password)));
    }

    [Fact]
    public void PinPolicy_ShouldSupportOneTimePlaintextMigration()
    {
        Assert.True(SecurityManager.VerifyPin("1234", "1234"));
        Assert.True(SecurityManager.PinNeedsRehash("1234"));

        string hash = SecurityManager.HashPin("1234");
        Assert.True(SecurityManager.VerifyPin("1234", hash));
        Assert.False(SecurityManager.VerifyPin("9999", hash));
        Assert.False(SecurityManager.PinNeedsRehash(hash));
    }

    [Fact]
    public void DpapiSecretStore_ShouldEncryptAndDetectTampering()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), $"ticketfy-dpapi-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "secret.bin");
        try
        {
            var store = new DpapiSecretStore(path);
            store.Write("sensitive-value");

            Assert.Equal("sensitive-value", store.Read());
            Assert.DoesNotContain("sensitive-value", Convert.ToBase64String(File.ReadAllBytes(path)));

            byte[] damaged = File.ReadAllBytes(path);
            damaged[damaged.Length / 2] ^= 0x5A;
            File.WriteAllBytes(path, damaged);
            Assert.ThrowsAny<Exception>(() => store.Read());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VerifyPassword_ShouldAuthenticateValidPasswordWithPbkdf2()
    {
        string pass = "SuperSecret#2026";
        string hash = SecurityManager.HashPassword(pass);

        bool isValid = SecurityManager.VerifyPassword(pass, hash);
        bool isInvalid = SecurityManager.VerifyPassword("WrongPassword", hash);

        Assert.True(isValid);
        Assert.False(isInvalid);
    }

    [Fact]
    public void VerifyPassword_ShouldSupportLegacySha256HashesForBackwardsCompatibility()
    {
        string pass = "LegacyAdminPass";

        // Compute plain SHA256 Base64 hash
        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(pass));
        string legacyB64Hash = Convert.ToBase64String(hashBytes);
        string legacyHexHash = Convert.ToHexString(hashBytes);

        // Verify Base64 legacy hash
        Assert.True(SecurityManager.VerifyPassword(pass, legacyB64Hash));
        Assert.False(SecurityManager.VerifyPassword("WrongPass", legacyB64Hash));

        // Verify Hex legacy hash
        Assert.True(SecurityManager.VerifyPassword(pass, legacyHexHash));
        Assert.False(SecurityManager.VerifyPassword("WrongPass", legacyHexHash));
    }

    [Fact]
    public void ProtectedSecret_ShouldCreateAndReuseKey_OnlyInTemporaryStorage()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ticketfy-test-key-{Guid.NewGuid():N}");
        try
        {
            string path = Path.Combine(directory, "key.dpapi");
            string key = SecurityManager.GetOrCreateProtectedSecret(path);
            Assert.Equal(32, Convert.FromBase64String(key).Length);
            Assert.Equal(key, SecurityManager.GetOrCreateProtectedSecret(path));
            Assert.NotEqual(key, SecurityManager.GetOrCreateProtectedSecret(Path.Combine(directory, "other.dpapi")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
