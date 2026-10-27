using Avalonia.Headless.XUnit;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Core.Security;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Settings;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseRoleEditorTests
{
    [AvaloniaFact]
    public async Task RoleEditor_PartialOverridesPreserveDefaultsAndExposeEveryBackendPermission()
    {
        var settings = new RoleSettings { Read = key => Task.FromResult<string?>(key.StartsWith("RolePermissions_") ? "{\"pos.checkout\":false}" : null) };
        var vm = new UsuariosSettingsViewModel(settingsService: settings);
        await vm.Initialization;
        await vm.LoadRolePermissionsAsync("CAJERO");
        Assert.Equal(PermissionCatalog.All.Order(), vm.RolePermissions.Select(p => p.Key).Order());
        Assert.False(vm.RolePermissions.Single(p => p.Key == PermissionCatalog.PosCheckout).IsGranted);
        Assert.True(vm.RolePermissions.Single(p => p.Key == PermissionCatalog.CashOpen).IsGranted);
        vm.IsAdminUnlocked = true;
        await vm.SaveRolePermissionsCommand.ExecuteAsync(null);
        Assert.Equal("RolePermissions_CAJERO", Assert.Single(settings.Writes).Key);
        Assert.Contains("\"cash.open\":true", settings.Writes[0].Value);
        vm.SelectedPermissionRole = "ADMINISTRADOR";
        await vm.LoadRolePermissionsAsync("ADMINISTRADOR");
        Assert.All(vm.RolePermissions.Where(p => PermissionCatalog.AdministratorRecovery.Contains(p.Key)),
            p => { Assert.True(p.IsGranted); Assert.False(p.IsEditable); });
    }

    [AvaloniaFact]
    public async Task RoleEditor_LoadingFailureAndLateResponseCannotSaveWrongRole()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new RoleSettings
        {
            Read = key => key == "RolePermissions_GERENTE" ? pending.Task : Task.FromResult<string?>(null)
        };
        var vm = new UsuariosSettingsViewModel(settingsService: settings) { IsAdminUnlocked = true };
        await vm.Initialization;
        vm.SelectedPermissionRole = "GERENTE";
        var old = vm.LoadRolePermissionsAsync("GERENTE");
        Assert.True(vm.IsPermissionLoading);
        await vm.SaveRolePermissionsCommand.ExecuteAsync(null);
        Assert.Empty(settings.Writes);
        vm.SelectedPermissionRole = "SUPERVISOR";
        await vm.LoadRolePermissionsAsync("SUPERVISOR");
        pending.SetResult("{\"cash.open\":false}");
        await old;
        Assert.True(vm.RolePermissions.Single(p => p.Key == PermissionCatalog.CashOpen).IsGranted);
        settings.Read = _ => Task.FromException<string?>(new IOException("Synthetic read failure"));
        await vm.LoadRolePermissionsAsync("SUPERVISOR");
        Assert.Empty(vm.RolePermissions);
        await vm.SaveRolePermissionsCommand.ExecuteAsync(null);
        Assert.Empty(settings.Writes);
        Assert.DoesNotContain("guardados", vm.PermissionFeedbackMessage);
    }

    [AvaloniaFact]
    public async Task RoleEditor_FailedMutationPreservesCatalogAndLateSuccessCannotRepopulateClearedSession()
    {
        var settings = new RoleSettings
        {
            Read = key => Task.FromResult<string?>(key == "CustomRolesList" ? "[\"AUDITOR\"]" : null),
            Write = (_, _) => Task.FromException(new IOException("Synthetic write failure"))
        };
        var vm = new UsuariosSettingsViewModel(settingsService: settings) { CustomRoleName = "INVENTARISTA" };
        await vm.Initialization;
        Assert.Contains("AUDITOR", vm.RoleOptions);
        await vm.AddCustomRoleCommand.ExecuteAsync(null);
        Assert.DoesNotContain("INVENTARISTA", vm.RoleOptions);
        Assert.StartsWith("No se guardó", vm.FeedbackMessage);
        await vm.DeleteCustomRoleCommand.ExecuteAsync("AUDITOR");
        Assert.Contains("AUDITOR", vm.RoleOptions);
        Assert.StartsWith("No se eliminó", vm.PermissionFeedbackMessage);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.Write = (_, _) => pending.Task;
        var saving = vm.AddCustomRoleCommand.ExecuteAsync(null);
        vm.ClearSessionSecrets();
        pending.SetResult();
        await saving;
        Assert.DoesNotContain("INVENTARISTA", vm.RoleOptions);
        Assert.Empty(vm.FeedbackMessage);
        Assert.Empty(vm.RolePermissions);
    }

    private sealed class RoleSettings : ISettingsService
    {
        public Func<string, Task<string?>> Read { get; set; } = _ => Task.FromResult<string?>(null);
        public Func<string, string, Task> Write { get; set; } = (_, _) => Task.CompletedTask;
        public List<(string Key, string Value)> Writes { get; } = [];
        public Task<string?> GetAsync(string key) => Read(key);
        public async Task SetAsync(string key, string value) { await Write(key, value); Writes.Add((key, value)); }
        public Task AddCustomRoleAsync(string name) => SetAsync("ADD_ROLE", name);
        public Task DeleteCustomRoleAsync(string name) => SetAsync("DELETE_ROLE", name);
        public Task<Dictionary<string, string>> GetAllAsync() => throw new NotSupportedException();
        public Task<AppSettings> GetAppSettingsAsync() => throw new NotSupportedException();
        public Task SaveAppSettingsAsync(AppSettings settings) => throw new NotSupportedException();
    }
}
