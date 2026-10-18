using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ticketfy.Core.Security;

namespace Ticketfy.ViewModels.Settings;

public class PermissionItemModel : ObservableObject
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    private bool _isGranted;
    public bool IsGranted
    {
        get => _isGranted;
        set => SetProperty(ref _isGranted, value);
    }
}

/// <summary>
/// Full CRUD for user accounts and RBAC Permission Matrix management.
/// Handles cashier/admin creation, 4-digit PIN management, custom role creation, custom role deletion,
/// password-gated admin lock screen, and granular permission editing per role across all 8 modules.
/// </summary>
public partial class UsuariosSettingsViewModel : ObservableObject
{
    private readonly IUserService? _userService;
    private readonly ISettingsService? _settingsService;
    private readonly IAuditService? _auditService;
    private readonly Func<string>? _auditActor;

    public static readonly HashSet<string> BaseRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADMINISTRADOR", "GERENTE", "SUPERVISOR", "CAJERO", "VENDEDOR"
    };

    public ObservableCollection<UserDto> Users { get; } = [];

    // ── Sub-tab navigation ──────────────────────────────────────────────────
    [ObservableProperty] private bool _isUsuariosTabVisible = true;
    [ObservableProperty] private bool _isPermisosTabVisible = false;

    [RelayCommand]
    private void SelectSubTab(object? param)
    {
        int subIndex = 0;
        if (param is int iVal) subIndex = iVal;
        else if (param is string sVal && int.TryParse(sVal, out int parsed)) subIndex = parsed;

        IsUsuariosTabVisible = (subIndex == 0);
        IsPermisosTabVisible = (subIndex == 1);
        FeedbackMessage = string.Empty;
        PermissionFeedbackMessage = string.Empty;
    }

    // ── Create new user form ───────────────────────────────────────────────
    [ObservableProperty] private string _newUsername = string.Empty;
    [ObservableProperty] private string _newFullName = string.Empty;
    [ObservableProperty] private string _newRole = "CAJERO";
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _newPasswordHint = string.Empty;
    [ObservableProperty] private string _newPin1 = string.Empty;
    [ObservableProperty] private string _newPin2 = string.Empty;
    [ObservableProperty] private string _newPin3 = string.Empty;
    [ObservableProperty] private string _newPin4 = string.Empty;

    public ObservableCollection<string> RoleOptions { get; } = ["ADMINISTRADOR", "GERENTE", "SUPERVISOR", "CAJERO", "VENDEDOR"];

    // ── Dynamic custom role creation & deletion (+ Nuevo Rol / Eliminar) ─────
    [ObservableProperty] private bool _isAddingCustomRole = false;
    [ObservableProperty] private string _customRoleName = string.Empty;

    // ── Admin Password Protection Gate for RBAC Permissions Tab ──────────
    [ObservableProperty] private bool _isAdminUnlocked = false;
    [ObservableProperty] private string _adminPasswordInput = string.Empty;
    [ObservableProperty] private string _adminGateErrorMessage = string.Empty;

    // ── Admin delete confirmation for user removal ──────────────────────────
    [ObservableProperty] private UserDto? _userToDelete;
    [ObservableProperty] private bool _isConfirmingAdminDelete = false;
    [ObservableProperty] private string _adminDeletePassword = string.Empty;
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    // ── Granular RBAC Permissions ──────────────────────────────────────────
    [ObservableProperty] private string _selectedPermissionRole = "CAJERO";
    public ObservableCollection<PermissionItemModel> RolePermissions { get; } = [];
    [ObservableProperty] private string _permissionFeedbackMessage = string.Empty;

    public bool IsSelectedRoleCustom => !BaseRoles.Contains(SelectedPermissionRole.Trim());

    [RelayCommand]
    private void ToggleAddCustomRole()
    {
        IsAddingCustomRole = !IsAddingCustomRole;
        CustomRoleName = string.Empty;
    }

    [RelayCommand]
    private async Task AddCustomRoleAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomRoleName)) return;

        string normalized = CustomRoleName.Trim().ToUpper();
        if (!RoleOptions.Contains(normalized))
        {
            RoleOptions.Add(normalized);
            await SaveCustomRolesListAsync();
        }
        NewRole = normalized;
        SelectedPermissionRole = normalized;
        CustomRoleName = string.Empty;
        IsAddingCustomRole = false;
        FeedbackMessage = $"¡Nuevo rol '{normalized}' agregado correctamente! Puede personalizar sus permisos a continuación.";
    }

    [RelayCommand]
    private async Task DeleteCustomRoleAsync(string? roleToDelete)
    {
        string target = string.IsNullOrWhiteSpace(roleToDelete) ? SelectedPermissionRole : roleToDelete;
        if (string.IsNullOrWhiteSpace(target)) return;

        string normalized = target.Trim().ToUpper();
        if (BaseRoles.Contains(normalized))
        {
            PermissionFeedbackMessage = "Los roles base del sistema (ADMINISTRADOR, GERENTE, SUPERVISOR, CAJERO, VENDEDOR) no se pueden eliminar.";
            return;
        }

        RoleOptions.Remove(normalized);
        await SaveCustomRolesListAsync();

        if (_settingsService != null)
        {
            try
            {
                await _settingsService.SetAsync($"RolePermissions_{normalized}", string.Empty);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error clearing permissions for deleted custom role {Role}", normalized);
            }
        }

        SelectedPermissionRole = "ADMINISTRADOR";
        PermissionFeedbackMessage = $"¡Rol personalizado '{normalized}' eliminado correctamente!";
        OnPropertyChanged(nameof(IsSelectedRoleCustom));
    }

    private async Task SaveCustomRolesListAsync()
    {
        if (_settingsService == null) return;
        try
        {
            var customRoles = RoleOptions.Where(r => !BaseRoles.Contains(r.Trim())).ToList();
            string json = JsonSerializer.Serialize(customRoles);
            await _settingsService.SetAsync("CustomRolesList", json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving CustomRolesList to SQLite settings");
        }
    }

    private async Task LoadCustomRolesListAsync()
    {
        if (_settingsService == null) return;
        try
        {
            string? json = await _settingsService.GetAsync("CustomRolesList");
            if (!string.IsNullOrWhiteSpace(json))
            {
                var customRoles = JsonSerializer.Deserialize<List<string>>(json);
                if (customRoles != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var role in customRoles)
                        {
                            string norm = role.Trim().ToUpper();
                            if (!string.IsNullOrWhiteSpace(norm) && !RoleOptions.Contains(norm))
                            {
                                RoleOptions.Add(norm);
                            }
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading CustomRolesList from SQLite settings");
        }
    }

    // ── Admin Password Gate Methods ────────────────────────────────────────
    [RelayCommand]
    private async Task UnlockAdminPermissionsAsync()
    {
        if (string.IsNullOrWhiteSpace(AdminPasswordInput))
        {
            AdminGateErrorMessage = "Por favor ingrese la contraseña del Administrador.";
            return;
        }

        if (_userService == null)
        {
            IsAdminUnlocked = false;
            AdminGateErrorMessage = "El servicio de autenticación no está disponible.";
            return;
        }

        try
        {
            var users = await _userService.GetAllAsync();
            var adminUser = users.FirstOrDefault(u => u.Rol.Equals("ADMINISTRADOR", StringComparison.OrdinalIgnoreCase) || u.Rol.Equals("ADMIN", StringComparison.OrdinalIgnoreCase));

            if (adminUser != null)
            {
                string? hash = await _userService.GetPasswordHashAsync(adminUser.Id);
                bool valid = !string.IsNullOrEmpty(hash)
                    && (Ticketfy.Core.Helpers.CryptoHelper.VerifyPassword(AdminPasswordInput, hash)
                        || Ticketfy.Services.Security.SecurityManager.VerifyPassword(AdminPasswordInput, hash));

                if (valid)
                {
                    IsAdminUnlocked = true;
                    AdminPasswordInput = string.Empty;
                    AdminGateErrorMessage = string.Empty;
                    return;
                }
            }
            AdminGateErrorMessage = "Contraseña de Administrador incorrecta.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error validating admin password for RBAC gate");
            AdminGateErrorMessage = "Error validando credenciales de administrador.";
        }
    }

    [RelayCommand]
    private void LockAdminPermissions()
    {
        IsAdminUnlocked = false;
        AdminPasswordInput = string.Empty;
        AdminGateErrorMessage = string.Empty;
    }

    public UsuariosSettingsViewModel(IUserService? userService = null, ISettingsService? settingsService = null, IAuditService? auditService = null, Func<string>? auditActor = null)
    {
        _userService = userService;
        _settingsService = settingsService;
        _auditService = auditService;
        _auditActor = auditActor;

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        await LoadCustomRolesListAsync();
        if (_userService != null) await LoadAsync();
        await LoadRolePermissionsAsync(SelectedPermissionRole);
    }

    partial void OnSelectedPermissionRoleChanged(string value)
    {
        OnPropertyChanged(nameof(IsSelectedRoleCustom));
        if (!string.IsNullOrWhiteSpace(value))
        {
            _ = LoadRolePermissionsAsync(value);
        }
    }

    public async Task LoadAsync()
    {
        if (_userService == null) return;
        try
        {
            var list = await _userService.GetAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Users.Clear();
                foreach (var u in list) Users.Add(u);
            });
        }
        catch (Exception ex) { Log.Error(ex, "UsuariosSettingsViewModel: error loading users"); }
    }

    public Task LoadUsersAsync() => LoadAsync();

    // ── RBAC Permission Engine ─────────────────────────────────────────────
    public async Task LoadRolePermissionsAsync(string roleName)
    {
        var allPermissions = GetDefaultPermissionCatalog();
        Dictionary<string, bool>? savedPermissions = null;

        if (_settingsService != null)
        {
            try
            {
                var json = await _settingsService.GetAsync($"RolePermissions_{roleName.ToUpper()}");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    savedPermissions = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading saved permissions for role {Role}", roleName);
            }
        }

        // Default Presets if no custom settings exist yet
        if (savedPermissions == null)
        {
            savedPermissions = GetDefaultRolePreset(roleName);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RolePermissions.Clear();
            foreach (var perm in allPermissions)
            {
                if (savedPermissions.TryGetValue(perm.Key, out bool granted))
                {
                    perm.IsGranted = granted;
                }
                RolePermissions.Add(perm);
            }
        });
    }

    [RelayCommand]
    private async Task SaveRolePermissionsAsync()
    {
        if (_settingsService == null) return;
        try
        {
            var dict = RolePermissions.ToDictionary(p => p.Key, p => p.IsGranted);
            string json = JsonSerializer.Serialize(dict);
            if (_auditService != null)
            {
                await _auditService.LogAsync(new Ticketfy.Data.Entities.AuditLogEntity
                {
                    UserId = _auditActor?.Invoke() ?? "SYSTEM",
                    EntityName = "RolePermissions",
                    EntityId = SelectedPermissionRole.ToUpperInvariant(),
                    ActionType = Ticketfy.Core.Enums.AuditActionType.RolePermissionsChanged,
                    RiskLevel = Ticketfy.Core.Enums.RiskLevel.Critical,
                    NewValue = json,
                    Reason = "Administrative RBAC matrix change"
                });
            }
            await _settingsService.SetAsync($"RolePermissions_{SelectedPermissionRole.ToUpper()}", json);
            PermissionFeedbackMessage = $"¡Permisos del rol '{SelectedPermissionRole}' guardados y aplicados correctamente en SQLite!";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving permissions for role {Role}", SelectedPermissionRole);
            PermissionFeedbackMessage = "Error al guardar los permisos del rol.";
        }
    }

    private static List<PermissionItemModel> GetDefaultPermissionCatalog() => [
        // ── Módulo 1: Punto de Venta (POS & Ventas) ──
        new() { Key = "pos.checkout", Category = "1. Punto de Venta (POS)", Title = "Procesar Cobro & Ventas", Description = "Permite realizar cobros de tickets en la terminal POS" },
        new() { Key = "pos.discount", Category = "1. Punto de Venta (POS)", Title = "Aplicar Descuentos Directos", Description = "Permite aplicar descuentos directos al carrito de compras" },
        new() { Key = "pos.modify_price", Category = "1. Punto de Venta (POS)", Title = "Modificar Precios Manualmente", Description = "Permite cambiar el precio unitario de un producto en el carrito" },
        new() { Key = "pos.cancel_sale", Category = "1. Punto de Venta (POS)", Title = "Cancelar / Anular Tickets", Description = "Permite cancelar tickets de venta iniciados o completados" },
        new() { Key = "pos.refund", Category = "1. Punto de Venta (POS)", Title = "Procesar Devoluciones & Reembolsos", Description = "Permite realizar devoluciones de mercancía y reembolsos de efectivo" },
        new() { Key = "pos.apply_points", Category = "1. Punto de Venta (POS)", Title = "Canjear Puntos de Fidelidad", Description = "Permite aplicar saldo del monedero electrónico del cliente" },
        new() { Key = "pos.credit_sale", Category = "1. Punto de Venta (POS)", Title = "Ventas a Crédito / Cuenta Corriente", Description = "Permite enviar ventas a la cuenta corriente del cliente" },

        // ── Módulo 2: Control de Caja & Arqueos ──
        new() { Key = "cash.open", Category = "2. Control de Caja & Arqueos", Title = "Apertura de Turno & Caja Inicial", Description = "Permite registrar el monto inicial y abrir turno de caja" },
        new() { Key = "cash.close", Category = "2. Control de Caja & Arqueos", Title = "Cierre de Caja & Corte (X / Z)", Description = "Permite realizar el arqueo final y cerrar turno de caja" },
        new() { Key = "cash.in_out", Category = "2. Control de Caja & Arqueos", Title = "Entradas & Salidas de Efectivo", Description = "Permite registrar retiros de efectivo, depósitos y gastos de caja" },
        new() { Key = "cash.history", Category = "2. Control de Caja & Arqueos", Title = "Ver Histórico de Turnos y Movimientos", Description = "Permite consultar cortes de caja previos e historial de salidas" },

        // ── Módulo 3: Catálogo & Inventario ──
        new() { Key = "inventory.view", Category = "3. Catálogo & Inventario", Title = "Consultar Catálogo & Stock", Description = "Permite visualizar el listado de productos y sus existencias" },
        new() { Key = "inventory.create", Category = "3. Catálogo & Inventario", Title = "Crear Nuevos Productos", Description = "Permite dar de alta nuevos artículos en el sistema" },
        new() { Key = "inventory.edit", Category = "3. Catálogo & Inventario", Title = "Editar Productos & Precios", Description = "Permite modificar nombres, costos, precios de venta y códigos" },
        new() { Key = "inventory.delete", Category = "3. Catálogo & Inventario", Title = "Eliminar Productos del Catálogo", Description = "Permite borrar productos existentes del inventario" },
        new() { Key = "inventory.stock_adjust", Category = "3. Catálogo & Inventario", Title = "Ajustes de Existencias & Entradas/Salidas", Description = "Permite modificar el stock físico mediante merma o auditoría" },

        // ── Módulo 4: Clientes & Crédito ──
        new() { Key = "customers.view", Category = "4. Clientes & Crédito", Title = "Consultar Directorio de Clientes", Description = "Permite ver la lista de clientes, historial de compras y puntos" },
        new() { Key = "customers.create_edit", Category = "4. Clientes & Crédito", Title = "Crear & Editar Clientes", Description = "Permite registrar nuevos clientes o editar sus datos de contacto" },
        new() { Key = "customers.delete", Category = "4. Clientes & Crédito", Title = "Eliminar Clientes", Description = "Permite remover clientes del directorio activo" },
        new() { Key = "customers.credit_limit", Category = "4. Clientes & Crédito", Title = "Otorgar & Modificar Límites de Crédito", Description = "Permite asignar crédito autorizado y plazos de pago a clientes" },

        // ── Módulo 5: Cotizaciones & Pedidos ──
        new() { Key = "quotes.manage", Category = "5. Cotizaciones & Pedidos", Title = "Gestionar Cotizaciones & Pedidos", Description = "Permite emitir, editar y convertir cotizaciones en venta" },

        // ── Módulo 6: Reportes & Finanzas ──
        new() { Key = "reports.view", Category = "6. Reportes & Finanzas", Title = "Ver Dashboard & Reportes de Ventas", Description = "Permite consultar gráficas, volúmenes de venta y productos top" },
        new() { Key = "reports.profits", Category = "6. Reportes & Finanzas", Title = "Ver Utilidad Neta & Márgenes", Description = "Permite visualizar ganancias reales, costos y márgenes de utilidad" },
        new() { Key = "reports.export", Category = "6. Reportes & Finanzas", Title = "Exportar Reportes a PDF / Excel", Description = "Permite descargar reportes contables y financieros" },

        // ── Módulo 7: Administración de Usuarios & Roles ──
        new() { Key = "users.view", Category = "7. Usuarios & Roles (RBAC)", Title = "Ver Usuarios Registrados", Description = "Permite ver el directorio de usuarios y cajeros activos" },
        new() { Key = "users.manage", Category = "7. Usuarios & Roles (RBAC)", Title = "Crear & Eliminar Usuarios / PINs", Description = "Permite dar de alta o baja cajeros y cambiar sus contraseñas" },
        new() { Key = "users.roles", Category = "7. Usuarios & Roles (RBAC)", Title = "Gestionar Roles & Matriz de Permisos", Description = "Permite crear nuevos roles y editar la matriz RBAC de accesos" },

        // ── Módulo 8: Ajustes & Configuración ──
        new() { Key = "settings.business", Category = "8. Ajustes Globales & Sistema", Title = "Configurar Empresa & Datos Fiscales", Description = "Permite editar la razón social, RFC y logotipo comercial" },
        new() { Key = "settings.hardware", Category = "8. Ajustes Globales & Sistema", Title = "Configurar Impresoras & Periféricos", Description = "Permite ajustar puertos POS, impresoras térmicas y básculas" },
        new() { Key = "settings.security", Category = "8. Ajustes Globales & Sistema", Title = "Configuración de Seguridad & Respaldos", Description = "Permite realizar backups de base de datos y llaves de encriptación" }
    ];

    private static Dictionary<string, bool> GetDefaultRolePreset(string role)
    {
        string normalized = role.Trim().ToUpperInvariant() switch { "ADMIN" => "ADMINISTRADOR", var value => value };
        return PermissionCatalog.GetDefaultPermissions(normalized);
    }

    [RelayCommand]
    private async Task SaveUserAsync()
    {
        if (_userService == null) return;
        if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewFullName))
        {
            FeedbackMessage = "Nombre y Usuario son obligatorios";
            return;
        }

        try
        {
            string finalPass = string.IsNullOrWhiteSpace(NewPassword)
                ? string.Empty
                : Ticketfy.Core.Helpers.CryptoHelper.HashPassword(NewPassword);

            string finalPin = $"{NewPin1}{NewPin2}{NewPin3}{NewPin4}";
            if (finalPin.Length != 4)
            {
                FeedbackMessage = "El PIN debe ser de 4 dígitos";
                return;
            }

            await _userService.SaveAsync(Guid.NewGuid().ToString(), NewFullName, NewUsername, NewRole, finalPass, finalPin, NewPasswordHint);
            await LoadAsync();

            NewUsername = string.Empty;
            NewFullName = string.Empty;
            NewPin1 = string.Empty; NewPin2 = string.Empty; NewPin3 = string.Empty; NewPin4 = string.Empty;
            NewPassword = string.Empty;
            NewPasswordHint = string.Empty;
            NewRole = "CAJERO";
            FeedbackMessage = "¡Cajero / Usuario registrado correctamente!";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UsuariosSettingsViewModel: error creating user");
            FeedbackMessage = "Error al crear usuario";
        }
    }

    [RelayCommand]
    private void RequestDeleteUser(UserDto user)
    {
        if (user == null) return;

        if (user.Role.ToUpper() == "ADMINISTRADOR" || user.Role.ToUpper() == "ADMIN")
        {
            UserToDelete = user;
            IsConfirmingAdminDelete = true;
            AdminDeletePassword = string.Empty;
            FeedbackMessage = "Para eliminar un administrador, confirma con su contraseña.";
        }
        else
        {
            _ = ConfirmDeleteUserAsync(user);
        }
    }

    [RelayCommand]
    private void CancelAdminDelete()
    {
        IsConfirmingAdminDelete = false;
        UserToDelete = null;
        AdminDeletePassword = string.Empty;
        FeedbackMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmAdminDeleteAsync()
    {
        if (UserToDelete == null || _userService == null) return;

        string savedHash = await _userService.GetPasswordHashAsync(UserToDelete.Id) ?? string.Empty;
        bool valid = !string.IsNullOrEmpty(savedHash)
            && Ticketfy.Services.Security.SecurityManager.VerifyPassword(AdminDeletePassword, savedHash);

        if (valid)
        {
            await ConfirmDeleteUserAsync(UserToDelete);
            CancelAdminDelete();
        }
        else
        {
            FeedbackMessage = "Contraseña de administrador incorrecta. No se puede eliminar.";
        }
    }

    private async Task ConfirmDeleteUserAsync(UserDto user)
    {
        if (_userService == null || user == null) return;
        try
        {
            await _userService.DeleteAsync(user.Id);
            await LoadAsync();
            WeakReferenceMessenger.Default.Send(new UserDeletedMessage(user.Id));
            FeedbackMessage = $"Usuario {user.FullName} eliminado exitosamente.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UsuariosSettingsViewModel: error deleting user");
            FeedbackMessage = "Error al eliminar usuario.";
        }
    }
}
