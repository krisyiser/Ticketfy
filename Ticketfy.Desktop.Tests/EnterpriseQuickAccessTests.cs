using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Auth;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Shell;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseCredentialBoundaryTests
{
    [Fact]
    public async Task QuickAccess_RequiresPrimaryProofInSameExecutionAndCannotApprovePrivilegedAction()
    {
        await using var db = Db();
        var users = new UserService(db, quickAccess: _quickAccess);
        var repository = new UserRepository(db, quickAccess: _quickAccess);
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.NotNull(await users.AuthenticateAsync("administrator", Password));
        Assert.NotNull(await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync("administrator", "4321"));
        Assert.Null(await new UserRepository(db, quickAccess: new QuickAccessSession()).ValidatePinAsync("administrator", "4321"));
        Assert.Null(await new UserRepository(db).ValidatePinAsync("administrator", "4321"));
        Assert.False(await users.VerifyManagerCredentialAsync(CommercialTestSession.UserId.ToString(), "4321", pin: true));
        Assert.NotNull(await repository.VerifyPasswordAsync("administrator", Password, supervisorOnly: true));
        _quickAccess.Clear();
        Assert.NotNull(await repository.VerifyPasswordAsync("administrator", Password, supervisorOnly: true));
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.NotNull(await repository.VerifyPasswordAsync("administrator", Password));
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
    }

    [Fact]
    public async Task QuickAccess_PinRehashAndRepeatedUseDoNotExtendFifteenMinuteDeadline()
    {
        await using var db = Db();
        var clock = new CredentialClock();
        var trust = new QuickAccessSession(clock);
        var users = new UserService(db, clock: clock, quickAccess: trust);
        var repository = new UserRepository(db, clock: clock, quickAccess: trust);
        Assert.NotNull(await users.AuthenticateAsync("administrator", Password));
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.False(SecurityManager.PinNeedsRehash((await db.Users.AsNoTracking().SingleAsync()).PinCode));
        clock.Utc = clock.Utc.AddMinutes(14);
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
        clock.Utc = clock.Utc.AddMinutes(1);
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.NotNull(await users.AuthenticateAsync("administrator", Password));
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
        clock.Utc = clock.Utc.AddMinutes(16);
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("pin")]
    [InlineData("username")]
    [InlineData("role")]
    [InlineData("inactive")]
    [InlineData("policy")]
    public async Task QuickAccess_AccountChangesInvalidatePreviouslyIssuedProof(string change)
    {
        await using var db = Db();
        var service = new UserService(db, quickAccess: _quickAccess);
        var repository = new UserRepository(db, quickAccess: _quickAccess);
        Assert.NotNull(await service.AuthenticateAsync("administrator", Password));
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
        var user = await db.Users.SingleAsync();
        string oldHash = user.PasswordHash, oldPin = user.PinCode, oldRole = user.RoleString;
        switch (change)
        {
            case "password": user.PasswordHash = SecurityManager.HashPassword(Password); break;
            case "pin": user.PinCode = SecurityManager.HashPin("4321"); break;
            case "username": user.Username = "renamed"; break;
            case "role": user.RoleString = "CAJERO"; break;
            case "inactive": user.IsActive = false; break;
            case "policy": user.PasswordPolicyVersion = 0; break;
        }
        await db.SaveChangesAsync();
        Assert.Null(await repository.ValidatePinAsync(user.Username, "4321"));
        // Restoring a prior profile must not resurrect a lease already rejected for a changed account.
        user.PasswordHash = oldHash; user.PinCode = oldPin; user.RoleString = oldRole;
        user.Username = "administrator"; user.IsActive = true; user.PasswordPolicyVersion = PasswordPolicy.CurrentVersion;
        await db.SaveChangesAsync();
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.NotNull(await service.AuthenticateAsync("administrator", Password));
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
    }

    [Fact]
    public async Task QuickAccess_DoesNotCrossDatabaseBoundariesEvenWithIdenticalAccountAndHashes()
    {
        await using var db = Db();
        Assert.NotNull(await new UserService(db, quickAccess: _quickAccess).AuthenticateAsync("administrator", Password));
        var original = await db.Users.AsNoTracking().SingleAsync();
        await using var otherConnection = new SqliteConnection("Data Source=:memory:");
        await otherConnection.OpenAsync();
        await using var other = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(otherConnection).Options);
        await other.Database.EnsureCreatedAsync();
        other.Users.Add(original); await other.SaveChangesAsync();
        Assert.Null(await new UserRepository(other, quickAccess: _quickAccess).ValidatePinAsync("administrator", "4321"));
        Assert.NotNull(await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync("administrator", "4321"));
    }

    [Fact]
    public async Task QuickAccess_ClockRollbackAndLogoutCannotRenewTrust()
    {
        await using var db = Db();
        var clock = new CredentialClock();
        var trust = new QuickAccessSession(clock);
        var service = new UserService(db, quickAccess: trust);
        var repository = new UserRepository(db, quickAccess: trust);
        var auth = new AuthService(service);
        Assert.True(await auth.LoginAsync("administrator", Password));
        clock.Utc = clock.Utc.AddMinutes(-1); // Isolate the lease clock from the already-tested durable attempt clock.
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
        clock.Utc = clock.Utc.AddMinutes(1);
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.True(await auth.LoginAsync("administrator", Password));
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
        auth.Logout();
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.True(await auth.LoginAsync("administrator", Password));
        Assert.False(await auth.LoginAsync("administrator", "incorrect"));
        Assert.Null(await repository.ValidatePinAsync("administrator", "4321"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickAccess_CancelledPrimaryRequestAndConcurrentRevocationCannotIssueTrust(bool revokeDuringSave)
    {
        await using var db = Db();
        var trust = new QuickAccessSession();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection)
            .AddInterceptors(new QuickAccessSaveObserver(() => { if (revokeDuringSave) trust.Clear(); })).Options;
        var service = new UserService(new TestDbContextFactory(options), quickAccess: trust);
        var result = await service.SignInAsync("administrator", Password, () => revokeDuringSave);
        if (!revokeDuringSave) Assert.Null(result.User);
        Assert.Null(await new UserRepository(db, quickAccess: trust).ValidatePinAsync("administrator", "4321"));
    }

    private sealed class QuickAccessSaveObserver(Action saved) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { saved(); return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task QuickAccess_FailedCredentialCommitCannotIssueTrustOrPersistRehash()
    {
        await using var db = Db();
        string original = (await db.Users.AsNoTracking().SingleAsync()).PasswordHash;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection)
            .AddInterceptors(new QuickAccessSaveObserver(() => throw new IOException("Synthetic credential commit failure"))).Options;
        await Assert.ThrowsAsync<IOException>(() => new UserService(new TestDbContextFactory(options), quickAccess: _quickAccess)
            .SignInAsync("administrator", Password));
        Assert.Equal(original, (await db.Users.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.Null(await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync("administrator", "4321"));
    }

    [AvaloniaFact]
    public async Task QuickAccess_UiSwitchAndBothUnlockSurfacesOfferPrimaryFallbackAfterPinExpiry()
    {
        await using var db = Db();
        var clock = new CredentialClock();
        var trust = new QuickAccessSession(clock);
        var repository = new UserRepository(db, clock: clock, quickAccess: trust);
        var session = new SessionManager();
        var switching = new SwitchUserPinDialogViewModel(session, repository) { UsePassword = false };
        await switching.InitializationTask;
        Assert.NotNull(switching.SelectedUser);
        foreach (string digit in new[] { "4", "3", "2", "1" }) await switching.AppendPinDigitCommand.ExecuteAsync(digit);
        Assert.Null(session.CurrentCashier); Assert.True(switching.UsePassword);
        switching.Password = Password;
        await switching.VerifyPasswordCommand.ExecuteAsync(null);
        Assert.Equal(CommercialTestSession.UserId, session.CurrentCashier!.Id); Assert.Empty(switching.Password);
        session.LockTerminal();
        using var unlock = new LockScreenViewModel(repository, session) { UsePassword = false, UnlockPin = "4321" };
        await unlock.UnlockTerminalCommand.ExecuteAsync(null);
        Assert.False(session.IsTerminalLocked); Assert.True(unlock.LastUnlockSucceeded);
        session.LockTerminal(); clock.Utc = clock.Utc.AddMinutes(15);
        unlock.UsePassword = false; unlock.UnlockPin = "4321";
        await unlock.UnlockTerminalCommand.ExecuteAsync(null);
        Assert.True(session.IsTerminalLocked); Assert.True(unlock.UsePassword);
        unlock.UnlockPin = Password;
        await unlock.UnlockTerminalCommand.ExecuteAsync(null);
        Assert.False(session.IsTerminalLocked); Assert.Empty(unlock.UnlockPin);
        session.LockTerminal();
        int closed = 0;
        var legacySurface = new LockScreenDialogViewModel(repository, session, () => closed++) { Password = Password };
        await legacySurface.VerifyPasswordCommand.ExecuteAsync(null);
        Assert.False(session.IsTerminalLocked); Assert.Equal(1, closed); Assert.Empty(legacySurface.Password);
    }

    [AvaloniaFact]
    public async Task QuickAccess_SupervisorUiRequiresPrimaryCredentialEvenWithValidQuickAccess()
    {
        await using var db = Db();
        var repository = new UserRepository(db, quickAccess: _quickAccess);
        Assert.NotNull(await repository.VerifyPasswordAsync("administrator", Password));
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
        var approvals = new List<bool>();
        var dialog = new SupervisorPinDialogViewModel(repository, "Sensitive action", (ok, _) => approvals.Add(ok))
        { Username = "administrator", Password = "4321" };
        await dialog.ApproveCommand.ExecuteAsync(null);
        Assert.Empty(approvals); Assert.Contains("PIN no autoriza", dialog.ErrorMessage, StringComparison.Ordinal);
        dialog.Password = Password;
        await dialog.ApproveCommand.ExecuteAsync(null);
        await dialog.ApproveCommand.ExecuteAsync(null);
        Assert.Equal([true], approvals); Assert.Empty(dialog.Password);
    }

    [Theory]
    [InlineData("ADMINISTRADOR", true)]
    [InlineData("GERENTE", true)]
    [InlineData("SUPERVISOR", true)]
    [InlineData("CAJERO", false)]
    public async Task QuickAccess_PrimarySupervisorApprovalRetainsAuthorizedRolesButNeverAcceptsPin(string role, bool allowed)
    {
        await using var db = Db();
        var user = await db.Users.SingleAsync(); user.RoleString = role; await db.SaveChangesAsync();
        var repository = new UserRepository(db, quickAccess: _quickAccess);
        Assert.Equal(allowed, await repository.VerifyPasswordAsync("administrator", Password, supervisorOnly: true) is not null);
        Assert.NotNull(await repository.VerifyPasswordAsync("administrator", Password));
        Assert.NotNull(await repository.ValidatePinAsync("administrator", "4321"));
        Assert.False(await new UserService(db, quickAccess: _quickAccess)
            .VerifyManagerCredentialAsync(user.Id.ToString(), "4321", pin: true));
    }

    [AvaloniaTheory]
    [InlineData("mode")]
    [InlineData("session")]
    [InlineData("dismiss")]
    public async Task QuickAccess_LatePrimaryUnlockCannotUnlockOrCreateTrustForDiscardedIntent(string invalidation)
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        session.LockTerminal();
        var blocker = new PrimaryCredentialBlocker();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(blocker).Options;
        var repo = new UserRepository(new TestDbContextFactory(options), quickAccess: _quickAccess);
        using var model = new LockScreenViewModel(repo, session) { UnlockPin = Password };
        var pending = model.UnlockTerminalCommand.ExecuteAsync(null);
        await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (invalidation == "mode") model.UsePassword = false;
        else if (invalidation == "session") session.ClearSession();
        else model.Dispose();
        blocker.Release.SetResult();
        await pending;
        Assert.False(model.LastUnlockSucceeded);
        Assert.Empty(model.UnlockErrorMessage); Assert.Empty(model.UnlockPin);
        if (invalidation == "session") Assert.Null(session.CurrentCashier);
        else Assert.True(session.IsTerminalLocked);
        Assert.Null(await new UserRepository(db, quickAccess: _quickAccess).ValidatePinAsync("administrator", "4321"));
    }

    private sealed class PrimaryCredentialBlocker : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"usuarios\"", StringComparison.Ordinal) && Interlocked.Exchange(ref _blocked, 1) == 0)
            { Started.SetResult(); await Release.Task; }
            return result;
        }
    }
}
