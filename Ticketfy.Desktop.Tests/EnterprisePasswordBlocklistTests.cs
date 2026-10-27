using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ticketfy.Core.Repositories;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Security;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseCredentialBoundaryTests
{
    // Public keyboard pattern from the pinned reference; never an operator's password.
    private const string PublicBlockedExample = "1q2w3e4r5t6y7u8i9o0p";

    [Theory]
    [InlineData(PublicBlockedExample)]
    [InlineData("1Q2W3E4R5T6Y7U8I9O0P")]
    [InlineData("  1q2w3e4r5t6y7u8i9o0p  ")]
    public async Task PasswordBlocklist_CommonWholeSecretCannotEnterThroughAccountWrites(string secret)
    {
        await using var db = Db();
        var users = new UserService(db, operations: _actor);
        string hash = (await db.Users.AsNoTracking().SingleAsync()).PasswordHash;
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.SaveAsync(Guid.NewGuid().ToString(), "Synthetic", "candidate", "CAJERO", secret, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.ChangePasswordAsync(CommercialTestSession.UserId.ToString(), secret));
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.UpgradeOwnPasswordAsync("administrator", Password, secret));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserRepository(db, operations: _actor).CreateUserAsync(
            new UserAccountDraft { FullName = "Synthetic", Username = "candidate", Password = secret }));
        Assert.Single(await db.Users.AsNoTracking().ToListAsync());
        Assert.Equal(hash, (await db.Users.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task PasswordBlocklist_PreviousPolicyNeedsValidPasswordProofBeforePinAndRequiresRotationOnlyWhenBlocked()
    {
        await using var db = Db();
        var user = await db.Users.SingleAsync(); user.PasswordPolicyVersion = 1;
        await db.SaveChangesAsync();
        var users = new UserService(db, quickAccess: _quickAccess);
        var repository = new UserRepository(db, quickAccess: _quickAccess);
        Assert.Null(await repository.ValidatePinAsync(user.Username, "4321"));
        var strong = await users.SignInAsync(user.Username, Password);
        Assert.NotNull(strong.User); Assert.False(strong.PasswordChangeRequired);
        Assert.Equal(2, (await db.Users.AsNoTracking().SingleAsync()).PasswordPolicyVersion);
        Assert.NotNull(await repository.ValidatePinAsync(user.Username, "4321"));
        db.ChangeTracker.Clear(); user = await db.Users.SingleAsync();
        user.PasswordHash = SecurityManager.HashPassword(PublicBlockedExample); user.PasswordPolicyVersion = 1;
        await db.SaveChangesAsync(); string originalHash = user.PasswordHash;
        var blocked = await users.SignInAsync(user.Username, PublicBlockedExample);
        Assert.Null(blocked.User); Assert.True(blocked.PasswordChangeRequired);
        Assert.Null(await repository.ValidatePinAsync(user.Username, "4321"));
        Assert.False(await users.VerifyManagerCredentialAsync(user.Id.ToString(), PublicBlockedExample));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _actor.RequireAsync(db, "inventory.view", false));
        Assert.Equal(originalHash, (await db.Users.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.True(await users.UpgradeOwnPasswordAsync(user.Username, PublicBlockedExample, Password));
        var upgraded = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(2, upgraded.PasswordPolicyVersion); Assert.Empty(upgraded.PinCode);
        string journal = JsonSerializer.Serialize(await db.TransactionJournal.ToListAsync());
        Assert.DoesNotContain(PublicBlockedExample, journal); Assert.DoesNotContain(originalHash, journal);
    }

    [Fact]
    public void PasswordBlocklist_EmbeddedReferenceIsCompleteAndDoesNotRejectSubstrings()
    {
        PasswordBlocklist.VerifyIntegrity();
        Assert.Equal(97746, PasswordBlocklist.Current.Count);
        Assert.True(PasswordBlocklist.Current.Contains("password"));
        Assert.True(PasswordBlocklist.Current.Contains(PublicBlockedExample));
        Assert.False(PasswordBlocklist.Current.Contains("La palabra password aparece junto a una brújula violeta"));
        Assert.Null(PasswordPolicy.RejectionReason("La palabra password aparece junto a una brújula violeta"));
        Assert.Null(PasswordPolicy.RejectionReason("🚀 Faro de cobre al otro lado del lago"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("size")]
    [InlineData("checksum")]
    [InlineData("duplicate")]
    [InlineData("order")]
    [InlineData("hex")]
    [InlineData("newline")]
    [InlineData("count")]
    public void PasswordBlocklist_InvalidReferenceCannotFallBackToAnEmptyList(string failure)
    {
        string[] values = [new('0', 64), new('F', 64)];
        if (failure == "duplicate") values[1] = values[0];
        if (failure == "order") Array.Reverse(values);
        if (failure == "hex") values[0] = new('Z', 64);
        byte[] bytes = Encoding.ASCII.GetBytes(string.Join("\n", values) + "\n");
        if (failure == "size") bytes = bytes[..^1];
        if (failure == "newline") bytes[64] = 13;
        string hash = failure == "checksum" ? new('0', 64) : Convert.ToHexString(SHA256.HashData(bytes));
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidOperationException>(() => PasswordBlocklist.Read(failure == "missing" ? null : stream, failure == "count" ? 100001 : 2, hash));
    }

    [Fact]
    public void PasswordBlocklist_BinarySearchChecksBothEndsAndNormalizesOnlyScreening()
    {
        string[] examples = ["Brújula violeta", "Otro ejemplo sintético", "Último ejemplo"];
        string[] digests = examples.Select(s => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToUpperInvariant()))))
            .Order(StringComparer.Ordinal).ToArray();
        byte[] bytes = Encoding.ASCII.GetBytes(string.Join("\n", digests) + "\n");
        using var stream = new MemoryStream(bytes);
        var reference = PasswordBlocklist.Read(stream, 3, Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.All(examples, value => Assert.True(reference.Contains(value)));
        Assert.True(reference.Contains("  bru\u0301jula violeta  "));
        Assert.False(reference.Contains("Frase desconocida número 27"));
    }
}
