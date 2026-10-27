using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Models;
using Ticketfy.Core.Services;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Auth;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseCredentialBoundaryTests
{
    [AvaloniaTheory]
    [InlineData("old")]
    [InlineData(PublicBlockedExample)]
    public async Task PasswordTransitionUi_RequiresFreshLoginAfterRotationAndDoubleClickCannotRotateTwice(string previousPassword)
    {
        await using var db = Db();
        await MakePrimaryWeakAsync(db);
        string previousHash = SecurityManager.HashPassword(previousPassword);
        await db.Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, previousHash).SetProperty(x => x.PasswordPolicyVersion, 1));
        var users = new TransitionUsers(new UserService(db));
        var auth = new AuthService(users);
        var session = new SessionManager();
        session.StartSession(new UserModel { Username = "previous" });
        var model = new LoginViewModel(auth, session, null!);
        var window = new Window { Content = new LoginView { DataContext = model }, Width = 800, Height = 720 };
        int logins = 0;
        model.LoginSuccessful += () => logins++;
        window.Show();
        try
        {
            model.Username = "administrator"; model.Password = previousPassword;
            await model.LoginCommand.ExecuteAsync(null);
            Assert.True(model.PasswordChangeRequired);
            Assert.Null(session.CurrentCashier);
            Assert.Null(auth.CurrentUser);
            Assert.False(auth.IsAuthenticated);
            Assert.Empty(model.Password);
            var boxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible).ToArray();
            Assert.Equal(4, boxes.Length);
            Assert.Equal(128, boxes[0].MaxLength);
            Assert.All(boxes.Skip(1), box => Assert.Equal(1024, box.MaxLength));
            Assert.All(boxes.Skip(1), box => Assert.Equal('•', box.PasswordChar));
            model.Password = previousPassword; model.NewPassword = Password; model.ConfirmNewPassword = "mismatch";
            await model.UpgradePasswordCommand.ExecuteAsync(null);
            Assert.Equal(0, users.UpgradeCalls);
            Assert.NotEmpty(model.ErrorMessage);
            model.ConfirmNewPassword = Password;
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Command == model.UpgradePasswordCommand);
            button.Command!.Execute(null);
            await model.UpgradePasswordCommand.ExecutionTask!;
            button.Command.Execute(null); // Disabled or already completed requests are harmless even if invoked directly.
            Assert.Equal(1, users.UpgradeCalls);
            Assert.False(model.PasswordChangeRequired);
            Assert.False(auth.IsAuthenticated);
            Assert.Null(session.CurrentCashier);
            Assert.Equal(0, logins);
            Assert.Empty(model.Password); Assert.Empty(model.NewPassword); Assert.Empty(model.ConfirmNewPassword);
            Assert.Contains("actualizada", model.InformationMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "PASSWORD_UPGRADED_BY_OWNER").ToListAsync());
            model.Password = Password;
            await model.LoginCommand.ExecuteAsync(null);
            await model.LoginCommand.ExecuteAsync(null);
            Assert.Equal(1, logins);
            Assert.Equal(CommercialTestSession.UserId, session.CurrentCashier!.Id);
            Assert.True(auth.IsAuthenticated);
            Assert.Empty(model.Username); Assert.Empty(model.Password);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("username")]
    [InlineData("cancel")]
    [InlineData("logout")]
    [InlineData("session")]
    public async Task PasswordTransitionUi_LateRotationCannotRestoreChallengeOrStartAChangedSession(string invalidation)
    {
        await using var db = Db();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var users = new TransitionUsers(new UserService(db)) { PendingUpgrade = completion.Task };
        var auth = new AuthService(users);
        var session = new SessionManager();
        var model = new LoginViewModel(auth, session, null!) { Username = "administrator", PasswordChangeRequired = true };
        model.Password = "old"; model.NewPassword = model.ConfirmNewPassword = Password;
        int logins = 0;
        model.LoginSuccessful += () => logins++;
        var pending = model.UpgradePasswordCommand.ExecuteAsync(null);
        Assert.False(pending.IsCompleted);
        await model.UpgradePasswordCommand.ExecuteAsync(null);
        Assert.Equal(1, users.UpgradeCalls);
        var other = new UserModel { Username = "another-session" };
        if (invalidation == "username") model.Username = "another-account";
        else if (invalidation == "cancel") model.CancelPasswordChangeCommand.Execute(null);
        else if (invalidation == "logout") model.EndSession();
        else session.StartSession(other);
        completion.SetResult(true);
        await pending;
        Assert.Equal(0, logins);
        Assert.False(auth.IsAuthenticated);
        Assert.Null(auth.CurrentUser);
        Assert.Same(invalidation == "session" ? other : null, session.CurrentCashier);
        Assert.DoesNotContain("actualizada", model.InformationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(model.Password); Assert.Empty(model.NewPassword); Assert.Empty(model.ConfirmNewPassword);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordTransitionUi_LateLoginCannotPublishIdentityOrChallengeAfterExternalSessionChange(bool weak)
    {
        await using var db = Db();
        var completion = new TaskCompletionSource<SignInResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var users = new TransitionUsers(new UserService(db)) { PendingSignIn = completion.Task };
        var auth = new AuthService(users);
        var session = new SessionManager();
        var model = new LoginViewModel(auth, session, null!) { Username = "administrator", Password = Password };
        int logins = 0;
        model.LoginSuccessful += () => logins++;
        var pending = model.LoginCommand.ExecuteAsync(null);
        var other = new UserModel { Username = "another-session" };
        session.StartSession(other);
        completion.SetResult(weak ? new(PasswordChangeRequired: true)
            : new(new UserDto(CommercialTestSession.UserId.ToString(), "administrator", "Synthetic", "ADMINISTRADOR", true)));
        await pending;
        Assert.Equal(0, logins);
        Assert.Same(other, session.CurrentCashier);
        Assert.False(model.PasswordChangeRequired);
        Assert.False(auth.IsAuthenticated);
        Assert.Null(auth.CurrentUser);
        Assert.Empty(model.Password);
    }

    [AvaloniaFact]
    public async Task PasswordTransitionUi_LostSuccessResponsePreservesNewPasswordAndProvidesSafeRecovery()
    {
        await using var db = Db();
        await MakePrimaryWeakAsync(db);
        var users = new TransitionUsers(new UserService(db)) { LoseUpgradeResponse = true };
        var auth = new AuthService(users);
        var session = new SessionManager();
        var model = new LoginViewModel(auth, session, null!) { Username = "administrator", Password = "old" };
        await model.LoginCommand.ExecuteAsync(null);
        model.Password = "old"; model.NewPassword = model.ConfirmNewPassword = Password;
        await model.UpgradePasswordCommand.ExecuteAsync(null);
        Assert.Null(session.CurrentCashier);
        Assert.False(auth.IsAuthenticated);
        Assert.Contains("nueva contraseña", model.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(model.Password); Assert.Empty(model.NewPassword); Assert.Empty(model.ConfirmNewPassword);
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "PASSWORD_UPGRADED_BY_OWNER").ToListAsync());
        model.CancelPasswordChangeCommand.Execute(null);
        model.Password = Password;
        await model.LoginCommand.ExecuteAsync(null);
        Assert.Equal(CommercialTestSession.UserId, session.CurrentCashier!.Id);
        Assert.Equal(1, users.UpgradeCalls);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordTransitionUi_FailureClearsSecretsAndDoesNotAuthenticate(bool invalidPolicy)
    {
        await using var db = Db();
        var exception = invalidPolicy ? (Exception)new InvalidOperationException("Synthetic policy rejection.") : new IOException("Synthetic failure.");
        var users = new TransitionUsers(new UserService(db)) { PendingUpgrade = Task.FromException<bool>(exception) };
        var auth = new AuthService(users);
        var session = new SessionManager();
        var model = new LoginViewModel(auth, session, null!) { Username = "administrator", PasswordChangeRequired = true };
        model.Password = "old"; model.NewPassword = model.ConfirmNewPassword = Password;
        await model.UpgradePasswordCommand.ExecuteAsync(null);
        Assert.Null(session.CurrentCashier);
        Assert.False(auth.IsAuthenticated);
        Assert.Empty(model.Password); Assert.Empty(model.NewPassword); Assert.Empty(model.ConfirmNewPassword);
        Assert.NotEmpty(model.ErrorMessage);
        Assert.True(model.CanUpgrade);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordTransitionUi_LateFailureCannotOverwriteAnotherSessionsScreen(bool rotation)
    {
        await using var db = Db();
        var signIn = new TaskCompletionSource<SignInResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var upgrade = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var users = new TransitionUsers(new UserService(db)) { PendingSignIn = signIn.Task, PendingUpgrade = upgrade.Task };
        var auth = new AuthService(users);
        var session = new SessionManager();
        var model = new LoginViewModel(auth, session, null!) { Username = "administrator", PasswordChangeRequired = rotation };
        model.Password = "old"; model.NewPassword = model.ConfirmNewPassword = Password;
        var pending = rotation ? model.UpgradePasswordCommand.ExecuteAsync(null) : model.LoginCommand.ExecuteAsync(null);
        var other = new UserModel { Username = "replacement" };
        session.StartSession(other);
        if (rotation) upgrade.SetException(new IOException("Synthetic late failure."));
        else signIn.SetException(new IOException("Synthetic late failure."));
        await pending;
        Assert.Empty(model.ErrorMessage);
        Assert.Same(other, session.CurrentCashier);
        Assert.False(auth.IsAuthenticated);
        Assert.Empty(model.Password);
    }

    private sealed class TransitionUsers(IUserService inner) : IUserService
    {
        public Task<SignInResult>? PendingSignIn { get; init; }
        public Task<bool>? PendingUpgrade { get; init; }
        public bool LoseUpgradeResponse { get; init; }
        public int UpgradeCalls { get; private set; }
        public Task<SignInResult> SignInAsync(string username, string password, Func<bool>? isRequestCurrent = null) => PendingSignIn ?? inner.SignInAsync(username, password, isRequestCurrent);
        public void ClearQuickAccess() => inner.ClearQuickAccess();
        public async Task<bool> UpgradeOwnPasswordAsync(string username, string currentPassword, string newPassword)
        {
            UpgradeCalls++;
            if (PendingUpgrade is { } pending) return await pending;
            bool result = await inner.UpgradeOwnPasswordAsync(username, currentPassword, newPassword);
            if (LoseUpgradeResponse) throw new IOException("Synthetic lost response.");
            return result;
        }
        public Task<UserDto?> AuthenticateAsync(string username, string password) => inner.AuthenticateAsync(username, password);
        public Task<List<UserDto>> GetAllAsync() => inner.GetAllAsync();
        public Task<bool> VerifyManagerCredentialAsync(string userId, string secret, bool pin = false) => inner.VerifyManagerCredentialAsync(userId, secret, pin);
        public Task<bool> ConfirmCurrentAdministratorAsync(string password) => inner.ConfirmCurrentAdministratorAsync(password);
        public Task SaveAsync(string id, string nombre, string username, string rol, string? password, string? pin, string? passwordHint = null) =>
            inner.SaveAsync(id, nombre, username, rol, password, pin, passwordHint);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public Task ChangePasswordAsync(string userId, string password) => inner.ChangePasswordAsync(userId, password);
        public Task ChangePinAsync(string userId, string pin) => inner.ChangePinAsync(userId, pin);
        public Task<string?> GetPasswordHintAsync(string username) => inner.GetPasswordHintAsync(username);
        public Task<bool> VerifyCredentialAsync(string userId, string secret, bool pin = false) => inner.VerifyCredentialAsync(userId, secret, pin);
    }
}
