using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Repositories;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Security;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseCredentialBoundaryTests
{
    [Theory]
    [InlineData("JOSÉ", "josé")]
    [InlineData("IN\u0303AKI", "iñaki")]
    [InlineData("ILKER", "ilker")]
    public async Task IdentityCompatibility_UsernameUsesUnicodeAndCultureIndependentComparison(string stored, string entered)
    {
        await using var db = Db();
        var admin = await db.Users.SingleAsync();
        admin.Username = stored; await db.SaveChangesAsync();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var users = new UserService(db, quickAccess: _quickAccess);
            Assert.Equal(admin.Id.ToString(), (await users.AuthenticateAsync(entered, Password))?.Id);
            Assert.Equal(admin.Id, (await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync(entered.ToUpperInvariant(), "4321"))?.Id);
            Assert.Equal(stored, (await db.Users.AsNoTracking().SingleAsync()).Username);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task IdentityCompatibility_DuplicateNamesAreRejectedAcrossServiceAndRepositoryIncludingInactiveAccounts()
    {
        await using var db = Db();
        db.Users.Add(new UserEntity { Username = "JOSÉ", FullName = "Legacy", IsActive = false });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserService(db, operations: _actor)
            .SaveAsync(Guid.NewGuid().ToString(), "Duplicate", "jose\u0301", "CAJERO", Password, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserRepository(db, operations: _actor)
            .CreateUserAsync(new UserAccountDraft { Username = "josé", FullName = "Duplicate", Password = Password }));
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task IdentityCompatibility_AmbiguousLegacyNamesFailClosedWithoutSelectingAnAccountOrRewritingHistory()
    {
        await using var db = Db();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password)));
        var first = new UserEntity { Username = "JOSÉ", FullName = "First", PasswordHash = hash, PinCode = "1234" };
        var second = new UserEntity { Username = "josé", FullName = "Second", PasswordHash = hash, PinCode = "1234" };
        db.Users.AddRange(first, second); await db.SaveChangesAsync();
        var users = new UserService(db);
        for (int i = 0; i < 5; i++) Assert.Null(await users.AuthenticateAsync("josé", Password));
        Assert.Null(await new UserRepository(db).ValidatePinAsync("JOSÉ", "1234"));
        Assert.Equal(3, await db.Users.CountAsync());
        Assert.True(await users.VerifyCredentialAsync(first.Id.ToString(), Password));
        Assert.Equal(5, (await db.CredentialAttempts.SingleAsync(a => a.Subject == "unknown-account")).Failures);
        Assert.Equal("JOSÉ", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == first.Id)).Username);
        Assert.Equal("josé", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == second.Id)).Username);
    }

    [Theory]
    [InlineData("username-long")]
    [InlineData("name-long")]
    [InlineData("password-long")]
    [InlineData("username-control")]
    [InlineData("name-control")]
    public async Task IdentityCompatibility_NewAccountsRejectUnusableInputWithoutJournalOrPartialAccount(string kind)
    {
        await using var db = Db();
        string username = kind == "username-long" ? new string('x', 129) : kind == "username-control" ? "user\u202Ename" : "valid";
        string name = kind == "name-long" ? new string('x', 257) : kind == "name-control" ? "name\nspoof" : "Valid";
        string password = kind == "password-long" ? new string('x', 1025) : Password;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserService(db, operations: _actor)
            .SaveAsync(Guid.NewGuid().ToString(), name, username, "CAJERO", password, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserRepository(db, operations: _actor)
            .CreateUserAsync(new UserAccountDraft { Username = username, FullName = name, Password = password }));
        Assert.Single(await db.Users.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task IdentityCompatibility_BlankPasswordEditCannotEraseExistingCredential()
    {
        await using var db = Db();
        var admin = await db.Users.SingleAsync();
        string before = admin.PasswordHash;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserService(db, operations: _actor)
            .SaveAsync(admin.Id.ToString(), admin.FullName, admin.Username, "ADMINISTRADOR", "   ", null));
        Assert.Equal(before, (await db.Users.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task IdentityCompatibility_PasswordShapedLikeHashIsAlwaysTreatedAsLiteralSecret()
    {
        await using var db = Db();
        string secret = HashShapedLiteral;
        var users = new UserService(db, operations: _actor);
        await users.SaveAsync(Guid.NewGuid().ToString(), "Literal", "literal", "CAJERO", secret, "1234");
        Assert.NotNull(await users.AuthenticateAsync("literal", secret));
        var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "literal");
        Assert.NotEqual(secret, stored.PasswordHash);
        Assert.False(SecurityManager.NeedsRehash(stored.PasswordHash));
    }

    [Fact]
    public async Task IdentityCompatibility_NewPinCannotBypassFourDigitValidationBySupplyingAHash()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<ArgumentException>(() => new UserService(db, operations: _actor)
            .SaveAsync(Guid.NewGuid().ToString(), "Invalid PIN", "pin", "CAJERO", Password, SecurityManager.HashPin("1234")));
        Assert.Single(await db.Users.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
    }

    [Fact]
    public async Task IdentityCompatibility_BoundariesLiteralSecretsRotationAndUnicodeFailuresAreConsistent()
    {
        await using var db = Db();
        var service = new UserService(db, operations: _actor);
        string username = new('u', 128), password = new string('p', 1007) + "tail-distinct1234";
        Assert.Equal(1024, password.Length);
        string id = Guid.NewGuid().ToString();
        await service.SaveAsync(id, new string('n', 256), username, "CAJERO", password, null);
        Assert.NotNull(await service.AuthenticateAsync(username, password));
        string hashShapedPassword = SecurityManager.HashPassword("Not-the-literal-secret");
        await service.ChangePasswordAsync(id, hashShapedPassword);
        Assert.NotNull(await service.AuthenticateAsync(username, hashShapedPassword));
        Assert.Null(await service.AuthenticateAsync(username, "Not-the-literal-secret"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangePasswordAsync(id, new string('p', 1025)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangePasswordAsync(id, "\uD800"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangePinAsync(id, SecurityManager.HashPin("1234")));
        Assert.Null(await service.AuthenticateAsync("\uD800", password));
        Assert.False(await service.VerifyCredentialAsync(id, "\uD800"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(id, "Name", "\uD800", "CAJERO", null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(id, "\uD800", username, "CAJERO", null, null));
        var repository = new UserRepository(db, operations: _actor);
        var created = await repository.CreateUserAsync(new UserAccountDraft
        {
            FullName = "Literal",
            Username = "literal-repo",
            Password = hashShapedPassword,
            Pin = "1234"
        });
        Assert.Empty(created.Pin4Digits);
        Assert.NotNull(await service.AuthenticateAsync(created.Username, hashShapedPassword));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateUserAsync(new UserAccountDraft
        { Id = Guid.Empty, Username = "invalid-id", FullName = "Invalid", Password = password }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveOrUpdateAdminUserAsync(new UserAccountDraft
        { Id = CommercialTestSession.UserId, Username = "administrator", FullName = "Admin", Role = "ADMIN", Password = new string('p', 1025) }));
    }

    [Fact]
    public async Task IdentityCompatibility_NewPasswordsRequireSixCodePointsAndWeakLegacyProofRequiresTransition()
    {
        await using var db = Db();
        var service = new UserService(db, operations: _actor);
        string id = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(id, "Short", "short", "CAJERO", "R7!ab", null));
        string fiveCodePoints = "\U0001F332\U0001F33Eabc"; // Seven UTF-16 units, only five code points.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(id, "Short", "short", "CAJERO", fiveCodePoints, null));
        string phrase = "prueba de frase";
        Assert.Equal(15, phrase.Length);
        await service.SaveAsync(id, "Phrase", "phrase", "CAJERO", phrase, null);
        Assert.NotNull(await service.AuthenticateAsync("phrase", phrase));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangePasswordAsync(id, "short"));
        var legacy = new UserEntity { Username = "legacy-short", FullName = "Legacy", PasswordHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old"))) };
        db.Users.Add(legacy); await db.SaveChangesAsync();
        Assert.Null(await service.AuthenticateAsync("legacy-short", "old"));
        Assert.True((await service.SignInAsync("legacy-short", "old")).PasswordChangeRequired);
        Assert.True(SecurityManager.NeedsRehash((await db.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id)).PasswordHash));
    }
}
