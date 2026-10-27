using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Models;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Settings;
using Ticketfy.Views.Settings;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseCredentialBoundaryTests
{
    [AvaloniaFact]
    public async Task AccountEditor_RenamesAmbiguousLegacyIdentityPreservesCredentialsAndExplicitlyRotatesOrDisablesPin()
    {
        await using var db = Db();
        var first = new UserEntity { Username = "JOSÉ", FullName = "First", PasswordHash = SecurityManager.HashPassword(Password), PinCode = SecurityManager.HashPin("1234") };
        db.Users.Add(first);
        db.Users.Add(new UserEntity { Username = "josé", FullName = "Second", PasswordHash = SecurityManager.HashPassword(Password), PinCode = "1234" });
        await db.SaveChangesAsync();
        string initialHash = first.PasswordHash, initialPin = first.PinCode;
        var service = new UserService(db, operations: _actor, quickAccess: _quickAccess);
        var vm = new UsuariosSettingsViewModel(service);
        await vm.Initialization;
        var window = new Window { Content = new UsuariosSettingsView { DataContext = vm }, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            vm.EditUserCommand.Execute(vm.Users.Single(u => u.Id == first.Id.ToString()));
            Assert.True(vm.IsEditingUser);
            Assert.Empty(vm.NewPassword); Assert.Empty(vm.NewPin1);
            vm.NewUsername = "josé.caja1";
            await vm.SaveUserCommand.ExecuteAsync(null);
            var saved = await db.Users.AsNoTracking().SingleAsync(u => u.Id == first.Id);
            Assert.Equal(initialHash, saved.PasswordHash); Assert.Equal(initialPin, saved.PinCode);
            Assert.Equal("josé.caja1", saved.Username);
            Assert.Equal(3, await db.Users.CountAsync());
            Assert.NotNull(await service.AuthenticateAsync("josé", Password));
            vm.EditUserCommand.Execute(vm.Users.Single(u => u.Id == first.Id.ToString()));
            vm.NewPassword = HashShapedLiteral;
            vm.NewPin1 = "9"; vm.NewPin2 = "8"; vm.NewPin3 = "7"; vm.NewPin4 = "6";
            await vm.SaveUserCommand.ExecuteAsync(null);
            Assert.NotNull(await service.AuthenticateAsync("JOSÉ.CAJA1", HashShapedLiteral));
            Assert.True(await service.VerifyCredentialAsync(first.Id.ToString(), "9876", pin: true));
            vm.EditUserCommand.Execute(vm.Users.Single(u => u.Id == first.Id.ToString()));
            vm.DisableEditedPin = true;
            await vm.SaveUserCommand.ExecuteAsync(null);
            Assert.False(await service.VerifyCredentialAsync(first.Id.ToString(), "9876", pin: true));
            vm.EditUserCommand.Execute(vm.Users.Single(u => u.Id == CommercialTestSession.UserId.ToString()));
            vm.NewRole = "CAJERO";
            await vm.SaveUserCommand.ExecuteAsync(null);
            Assert.Contains("administrador activo", vm.FeedbackMessage);
            Assert.True(vm.IsEditingUser);
            Assert.Equal("ADMINISTRADOR", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == CommercialTestSession.UserId)).RoleString);
            vm.CancelUserEditCommand.Execute(null);
            Assert.False(vm.IsEditingUser); Assert.Empty(vm.NewUsername);
            Assert.All(await db.TransactionJournal.ToListAsync(), j => Assert.Equal(CommercialTestSession.UserId.ToString(), j.CashierUserId));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AccountEditor_PostCommitReadFailureDoesNotRepeatCreationAndLateSaveCannotRestoreClearedSession()
    {
        await using var db = Db();
        var real = new UserService(db, operations: _actor);
        var wrapper = new AccountEditorUsers(real);
        var vm = new UsuariosSettingsViewModel(wrapper);
        await vm.Initialization;
        vm.NewFullName = "New"; vm.NewUsername = "new"; vm.NewPassword = Password;
        vm.NewPin1 = "1"; vm.NewPin2 = "2"; vm.NewPin3 = "3"; vm.NewPin4 = "4";
        wrapper.FailRead = true;
        await vm.SaveUserCommand.ExecuteAsync(null);
        Assert.Contains("Usuario registrado", vm.FeedbackMessage);
        Assert.Contains("No se pudo actualizar", vm.FeedbackMessage);
        Assert.Empty(vm.Users); Assert.Empty(vm.NewPassword);
        await vm.SaveUserCommand.ExecuteAsync(null);
        Assert.Equal(1, wrapper.Saves);
        wrapper.FailRead = false;
        await vm.LoadUsersAsync();
        vm.EditUserCommand.Execute(vm.Users.Single(u => u.Username == "new"));
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wrapper.AfterSave = () => pending.Task;
        vm.NewFullName = "Changed";
        var saving = vm.SaveUserCommand.ExecuteAsync(null);
        Assert.True(vm.IsUserSaving);
        vm.ClearSessionSecrets();
        pending.SetResult();
        await saving;
        Assert.Empty(vm.FeedbackMessage); Assert.Empty(vm.Users); Assert.Empty(vm.NewPassword);
        Assert.False(vm.IsEditingUser); Assert.False(vm.IsUserSaving);
        Assert.Equal(2, await db.Users.CountAsync());
    }

    [AvaloniaFact]
    public async Task AccountEditor_InactiveAccountRemainsInactiveAndInvalidPinOrUsernameCannotSave()
    {
        await using var db = Db();
        var service = new UserService(db, operations: _actor);
        string id = Guid.NewGuid().ToString();
        await service.SaveAsync(id, "Inactive", "inactive", "CAJERO", Password, "1234");
        await service.DeleteAsync(id);
        var vm = new UsuariosSettingsViewModel(service);
        await vm.Initialization;
        vm.EditUserCommand.Execute(vm.Users.Single(u => u.Id == id));
        vm.NewFullName = "Still inactive";
        await vm.SaveUserCommand.ExecuteAsync(null);
        Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(id))).IsActive);
        vm.EditUserCommand.Execute(vm.Users.Single(u => u.Id == id));
        vm.NewPin1 = "a"; vm.NewPin2 = "2"; vm.NewPin3 = "3"; vm.NewPin4 = "4";
        await vm.SaveUserCommand.ExecuteAsync(null);
        Assert.Contains("cuatro dígitos", vm.FeedbackMessage);
        vm.NewPin1 = vm.NewPin2 = vm.NewPin3 = vm.NewPin4 = "";
        vm.NewUsername = new string('x', 129);
        await vm.SaveUserCommand.ExecuteAsync(null);
        Assert.Contains("128", vm.FeedbackMessage);
        Assert.Equal("inactive", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(id))).Username);
    }

    [AvaloniaFact]
    public async Task OnboardingAccount_UsesExplicitLoadedIdLiteralSecretAndRedactedResult()
    {
        await using var db = Db();
        UserModel? returned = null; int advances = 0;
        var vm = new FirstTimeSetupViewModel(new UserRepository(db, operations: _actor), null!, () => advances++, onAdminCreated: user => returned = user);
        await vm.InitializationTask;
        Assert.Equal("administrator", vm.AdminUsername);
        vm.AdminPassword = HashShapedLiteral;
        vm.AdminPin1 = "1"; vm.AdminPin2 = "2"; vm.AdminPin3 = "3"; vm.AdminPin4 = "4";
        var window = new Window { Content = new FirstTimeSetupView { DataContext = vm } };
        window.Show();
        try
        {
            await vm.CreateAdminAccountCommand.ExecuteAsync(null);
            Assert.Equal(1, advances); Assert.NotNull(returned);
            Assert.Equal(CommercialTestSession.UserId, returned.Id);
            Assert.Empty(returned.Pin4Digits); Assert.Empty(vm.AdminPassword); Assert.Empty(vm.AdminPin);
            Assert.NotNull(await new UserService(db).AuthenticateAsync("administrator", HashShapedLiteral));
        }
        finally { window.Close(); }
    }

    private sealed class AccountEditorUsers(IUserService inner) : IUserService
    {
        public bool FailRead { get; set; }
        public Func<Task>? AfterSave { get; set; }
        public int Saves { get; private set; }
        public Task<List<UserDto>> GetAllAsync() => FailRead ? Task.FromException<List<UserDto>>(new InvalidOperationException("Synthetic")) : inner.GetAllAsync();
        public async Task SaveAsync(string id, string nombre, string username, string rol, string? password, string? pin, string? passwordHint = null)
        { await inner.SaveAsync(id, nombre, username, rol, password, pin, passwordHint); Saves++; if (AfterSave is not null) await AfterSave(); }
        public Task<UserDto?> AuthenticateAsync(string username, string password) => inner.AuthenticateAsync(username, password);
        public Task<SignInResult> SignInAsync(string username, string password, Func<bool>? isRequestCurrent = null) => inner.SignInAsync(username, password, isRequestCurrent);
        public void ClearQuickAccess() => inner.ClearQuickAccess();
        public Task<bool> UpgradeOwnPasswordAsync(string username, string currentPassword, string newPassword) =>
            inner.UpgradeOwnPasswordAsync(username, currentPassword, newPassword);
        public Task<bool> VerifyManagerCredentialAsync(string userId, string secret, bool pin = false) => inner.VerifyManagerCredentialAsync(userId, secret, pin);
        public Task<bool> ConfirmCurrentAdministratorAsync(string password) => inner.ConfirmCurrentAdministratorAsync(password);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public Task ChangePasswordAsync(string userId, string password) => inner.ChangePasswordAsync(userId, password);
        public Task ChangePinAsync(string userId, string pin) => inner.ChangePinAsync(userId, pin);
        public Task<string?> GetPasswordHintAsync(string username) => inner.GetPasswordHintAsync(username);
        public Task<bool> VerifyCredentialAsync(string userId, string secret, bool pin = false) => inner.VerifyCredentialAsync(userId, secret, pin);
    }
}
