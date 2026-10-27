using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Core.Models;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Navigation;
using Ticketfy.ViewModels.Settings;
using Ticketfy.Views.Dialogs;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseCredentialUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickAccessSurfaces_OfferMaskedPrimaryPasswordAndClearItWhenChangingMode(bool locking)
    {
        var user = new UserModel { Username = "cashier", FullName = "Synthetic cashier" };
        var session = new SessionManager();
        session.StartSession(user);
        var repository = new DelayedRepository(user, Task.FromResult<UserModel?>(user));
        var switching = new SwitchUserPinDialogViewModel(session, repository);
        await switching.InitializationTask;
        if (locking) session.LockTerminal();
        var unlock = new LockScreenDialogViewModel(repository, session, () => { });
        var window = new Window
        {
            Width = 1024,
            Height = 768,
            Content = locking ? new LockScreenDialog { DataContext = unlock }
                : new SwitchUserPinDialog { DataContext = switching }
        };
        window.Show();
        try
        {
            var field = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), x => x.PasswordChar == '•');
            Assert.True(field.IsEffectivelyVisible);
            Assert.Equal(1024, field.MaxLength);
            field.Text = "Synthetic primary credential";
            Assert.Equal(field.Text, locking ? unlock.Password : switching.Password);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == field.Text);
            var mode = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>());
            Assert.True(mode.IsChecked);
            mode.IsChecked = false;
            Assert.False(locking ? unlock.UsePassword : switching.UsePassword);
            Assert.Empty(locking ? unlock.Password : switching.Password);
            Assert.False(field.IsEffectivelyVisible);
            mode.IsChecked = true;
            Assert.True(field.IsEffectivelyVisible);
        }
        finally { switching.OnDismissed(); unlock.OnDismissed(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SupervisorApproval_MasksPasswordAndCannotApproveAfterOverlayReplacement()
    {
        var pending = new TaskCompletionSource<UserModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admin = new UserModel { Username = "supervisor", Role = SystemRole.ADMIN };
        var repo = new DelayedRepository(admin, pending.Task);
        var results = new List<bool>();
        var model = new SupervisorPinDialogViewModel(repo, "Synthetic approval", (ok, _) => results.Add(ok)) { Username = "supervisor" };
        var coordinator = new DialogCoordinator();
        coordinator.ShowDialog(model);
        var window = new Window { Content = new SupervisorPinDialog { DataContext = model } };
        window.Show();
        try
        {
            model.ErrorMessage = "Synthetic explanation requiring multiple lines for password revalidation.";
            var notice = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == model.ErrorMessage);
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, notice.TextWrapping);
            Assert.True(double.IsNaN(notice.Height));
            model.Password = "Synthetic supervised password";
            var field = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), t => t.PasswordChar == '•');
            Assert.Equal(1024, field.MaxLength);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == model.Password);
            var verifying = model.ApproveCommand.ExecuteAsync(null);
            Assert.False(verifying.IsCompleted);
            var replacement = new Placeholder();
            coordinator.ShowDialog(replacement);
            Assert.Equal([false], results);
            pending.SetResult(admin);
            await verifying;
            Assert.Equal([false], results);
            Assert.Empty(model.Password);
            Assert.Same(replacement, coordinator.ActiveDialogViewModel);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("cancel")]
    [InlineData("session")]
    [InlineData("selection")]
    public async Task SwitchUserPin_LateVerificationCannotSwitchAfterIntentIsInvalidated(string invalidation)
    {
        var pending = new TaskCompletionSource<UserModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new UserModel { Username = "original" };
        var target = new UserModel { Username = "target" };
        var replacement = new UserModel { Username = "replacement" };
        var session = new SessionManager();
        session.StartSession(original);
        var results = new List<bool>();
        var vm = new SwitchUserPinDialogViewModel(session, new DelayedRepository(target, pending.Task), results.Add);
        Assert.Same(target, vm.SelectedUser);
        vm.UsePassword = false;
        foreach (string digit in new[] { "4", "3", "2" }) await vm.AppendPinDigitCommand.ExecuteAsync(digit);
        var verifying = vm.AppendPinDigitCommand.ExecuteAsync("1");
        Assert.False(verifying.IsCompleted);
        if (invalidation == "cancel") vm.CloseModalCommand.Execute(null);
        else if (invalidation == "session") session.StartSession(replacement);
        else vm.SelectedUser = replacement;
        pending.SetResult(target);
        await verifying;
        Assert.Same(invalidation == "session" ? replacement : original, session.CurrentCashier);
        Assert.DoesNotContain(true, results);
        vm.OnDismissed();
        Assert.Empty(vm.EnteredPin);
    }

    [AvaloniaFact]
    public async Task SwitchUserPin_ConfirmsOnlySelectedIdentityAndEmitsOneResult()
    {
        var selected = new UserModel { Username = "cashier" };
        var session = new SessionManager();
        var results = new List<bool>();
        var vm = new SwitchUserPinDialogViewModel(session, new DelayedRepository(selected, Task.FromResult<UserModel?>(selected)), results.Add);
        vm.UsePassword = false;
        foreach (string digit in new[] { "4", "3", "2", "1" }) await vm.AppendPinDigitCommand.ExecuteAsync(digit);
        vm.OnDismissed();
        Assert.Equal([true], results);
        Assert.Same(selected, session.CurrentCashier);
        Assert.Empty(vm.EnteredPin);
    }

    [AvaloniaFact]
    public async Task AdministrativeCredentialResponse_CannotUnlockOrDeleteAfterSessionSecretsAreCleared()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var users = new DelayedUserService(pending.Task);
        var model = new UsuariosSettingsViewModel(users) { AdminPasswordInput = "synthetic-secret" };
        var unlocking = model.UnlockAdminPermissionsCommand.ExecuteAsync(null);
        model.ClearSessionSecrets();
        pending.SetResult(true);
        await unlocking;
        Assert.False(model.IsAdminUnlocked);
        Assert.Empty(model.AdminPasswordInput);
        var deleting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        users.Pending = deleting.Task;
        model.RequestDeleteUserCommand.Execute(new UserDto(Guid.NewGuid().ToString(), "other-admin", "Other", "ADMINISTRADOR", true));
        model.AdminDeletePassword = "synthetic-secret";
        var removal = model.ConfirmAdminDeleteCommand.ExecuteAsync(null);
        model.ClearSessionSecrets();
        deleting.SetResult(true);
        await removal;
        Assert.Equal(0, users.Deletes);
        Assert.Empty(model.AdminDeletePassword);
    }

    private sealed class Placeholder : ObservableObject { }
    private sealed class DelayedRepository(UserModel user, Task<UserModel?> pending) : IUserRepository
    {
        public Task<List<UserModel>> GetActiveUsersAsync() => Task.FromResult(new List<UserModel> { user });
        public Task<UserModel?> ValidatePinAsync(string username, string pin4Digits) => pending;
        public Task<UserModel?> VerifyPasswordAsync(string username, string password, bool supervisorOnly = false, Func<bool>? isRequestCurrent = null) => pending;
        public Task<bool> HasAnyUsersAsync() => throw new NotSupportedException();
        public Task<UserModel> CreateUserAsync(UserAccountDraft user) => throw new NotSupportedException();
        public Task<UserModel> SaveOrUpdateAdminUserAsync(UserAccountDraft user) => throw new NotSupportedException();
        public Task<UserDto?> GetAdminProfileAsync() => throw new NotSupportedException();
    }
    private sealed class DelayedUserService(Task<bool> pending) : IUserService
    {
        public Task<bool> Pending { get; set; } = pending;
        public int Deletes { get; private set; }
        public Task<bool> ConfirmCurrentAdministratorAsync(string password) => Pending;
        public Task DeleteAsync(string id) { Deletes++; return Task.CompletedTask; }
        public Task<List<UserDto>> GetAllAsync() => Task.FromResult(new List<UserDto>());
        public Task<UserDto?> AuthenticateAsync(string username, string password) => throw new NotSupportedException();
        public Task<SignInResult> SignInAsync(string username, string password, Func<bool>? isRequestCurrent = null) => throw new NotSupportedException();
        public void ClearQuickAccess() { }
        public Task<bool> UpgradeOwnPasswordAsync(string username, string currentPassword, string newPassword) => throw new NotSupportedException();
        public Task<bool> VerifyManagerCredentialAsync(string userId, string secret, bool pin = false) => throw new NotSupportedException();
        public Task SaveAsync(string id, string nombre, string username, string rol, string? passwordHash, string? pinHash, string? passwordHint = null) => throw new NotSupportedException();
        public Task ChangePasswordAsync(string userId, string passwordHash) => throw new NotSupportedException();
        public Task ChangePinAsync(string userId, string pinHash) => throw new NotSupportedException();
        public Task<string?> GetPasswordHintAsync(string username) => throw new NotSupportedException();
        public Task<bool> VerifyCredentialAsync(string userId, string secret, bool pin = false) => throw new NotSupportedException();
    }
}
