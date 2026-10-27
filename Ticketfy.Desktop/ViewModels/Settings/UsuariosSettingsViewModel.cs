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
    public bool IsEditable { get; set; } = true;

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
    private long _securityVersion;
    private long _permissionLoadVersion;
    private string? _loadedPermissionRole;
    private bool _roleMutation;
    [ObservableProperty] private bool _isPermissionLoading;
    public Task Initialization { get; }
    public void ClearSessionSecrets()
    {
        ++_securityVersion;
        ++_permissionLoadVersion;
        _loadedPermissionRole = null;
        ResetUserEditor();
        IsPermissionLoading = false;
        IsAdminUnlocked = false;
        NewUsername = NewFullName = NewPassword = NewPasswordHint = NewPin1 = NewPin2 = NewPin3 = NewPin4 = AdminPasswordInput = AdminDeletePassword = CustomRoleName = string.Empty;
        UserToDelete = null; IsConfirmingAdminDelete = false; Users.Clear(); RolePermissions.Clear();
        RoleOptions.Clear();
        foreach (string role in BaseRoles) RoleOptions.Add(role);
        FeedbackMessage = PermissionFeedbackMessage = "";
    }

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
        if (_roleMutation || string.IsNullOrWhiteSpace(CustomRoleName)) return;
        long version = _securityVersion;
        _roleMutation = true;
        string normalized = CustomRoleName.Trim().ToUpperInvariant();
        try
        {
            if (_settingsService == null) throw new InvalidOperationException("Servicio de configuración no disponible.");
            if (BaseRoles.Contains(normalized) || normalized == "ADMIN") throw new InvalidOperationException("El rol ya existe como rol base.");
            await _settingsService.AddCustomRoleAsync(normalized);
            if (version != _securityVersion) return;
            if (!RoleOptions.Contains(normalized)) RoleOptions.Add(normalized);
            NewRole = normalized;
            SelectedPermissionRole = normalized;
            CustomRoleName = string.Empty;
            IsAddingCustomRole = false;
            FeedbackMessage = $"Rol '{normalized}' guardado. Configure sus permisos antes de asignarlo.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error creating custom role");
            if (version == _securityVersion) FeedbackMessage = "No se guardó el rol. Revise el nombre, sus permisos y la configuración.";
        }
        finally { _roleMutation = false; }
    }

    [RelayCommand]
    private async Task DeleteCustomRoleAsync(string? roleToDelete)
    {
        string target = string.IsNullOrWhiteSpace(roleToDelete) ? SelectedPermissionRole : roleToDelete;
        if (string.IsNullOrWhiteSpace(target)) return;

        if (_roleMutation) return;
        string normalized = target.Trim().ToUpperInvariant();
        if (BaseRoles.Contains(normalized))
        {
            PermissionFeedbackMessage = "Los roles base del sistema (ADMINISTRADOR, GERENTE, SUPERVISOR, CAJERO, VENDEDOR) no se pueden eliminar.";
            return;
        }

        long version = _securityVersion;
        _roleMutation = true;
        try
        {
            if (_settingsService == null) throw new InvalidOperationException("Servicio de configuración no disponible.");
            // The backend removes the policy in the same transaction and refuses assigned roles.
            await _settingsService.DeleteCustomRoleAsync(normalized);
            if (version != _securityVersion) return;
            RoleOptions.Remove(normalized);
            if (NewRole == normalized) NewRole = "CAJERO";
            SelectedPermissionRole = "ADMINISTRADOR";
            PermissionFeedbackMessage = $"Rol '{normalized}' eliminado.";
            OnPropertyChanged(nameof(IsSelectedRoleCustom));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting custom role");
            if (version == _securityVersion) PermissionFeedbackMessage = "No se eliminó el rol. Reasigne primero todas sus cuentas y compruebe sus permisos.";
        }
        finally { _roleMutation = false; }
    }

    private async Task LoadCustomRolesListAsync()
    {
        if (_settingsService == null) return;
        long version = _securityVersion;
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
                        if (version != _securityVersion) return;
                        foreach (var role in customRoles)
                        {
                            string norm = role.Trim().ToUpperInvariant();
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

        long version = _securityVersion;
        IsAdminUnlocked = false;
        try
        {
            bool valid = await _userService.ConfirmCurrentAdministratorAsync(AdminPasswordInput);
            if (version != _securityVersion) return;
            AdminPasswordInput = string.Empty;
            if (valid)
            {
                IsAdminUnlocked = true;
                AdminGateErrorMessage = string.Empty;
                if (_loadedPermissionRole != SelectedPermissionRole.Trim().ToUpperInvariant())
                    await LoadRolePermissionsAsync(SelectedPermissionRole);
                return;
            }
            AdminGateErrorMessage = "Contraseña de Administrador incorrecta.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error validating admin password for RBAC gate");
            if (version == _securityVersion)
            { IsAdminUnlocked = false; AdminPasswordInput = ""; AdminGateErrorMessage = "Error validando credenciales de administrador."; }
        }
    }

    [RelayCommand]
    private void LockAdminPermissions()
    {
        ++_securityVersion;
        IsAdminUnlocked = false;
        AdminPasswordInput = string.Empty;
        AdminGateErrorMessage = string.Empty;
    }

    public UsuariosSettingsViewModel(IUserService? userService = null, ISettingsService? settingsService = null, IAuditService? auditService = null, Func<string>? auditActor = null, bool loadOnCreate = true)
    {
        _userService = userService;
        _settingsService = settingsService;

        Initialization = loadOnCreate ? InitAsync() : Task.CompletedTask;
    }

    public async Task InitAsync()
    {
        long version = _securityVersion;
        await LoadCustomRolesListAsync();
        if (version != _securityVersion) return;
        if (_userService != null) await LoadAsync();
        if (version != _securityVersion) return;
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
        long version = _securityVersion;
        _usersReadFailed = false;
        Users.Clear();
        try
        {
            var list = await _userService.GetAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _securityVersion) return;
                Users.Clear();
                foreach (var u in list) Users.Add(u);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UsuariosSettingsViewModel: error loading users");
            if (version == _securityVersion)
            {
                Users.Clear(); _usersReadFailed = true;
                FeedbackMessage = "No se pudo actualizar la lista de usuarios. Verifique su sesión e intente de nuevo.";
            }
        }
    }

    public Task LoadUsersAsync() => LoadAsync();

    // ── RBAC Permission Engine ─────────────────────────────────────────────
    public async Task LoadRolePermissionsAsync(string roleName)
    {
        long load = ++_permissionLoadVersion, security = _securityVersion;
        string role = roleName.Trim().ToUpperInvariant();
        _loadedPermissionRole = null;
        RolePermissions.Clear();
        IsPermissionLoading = true;
        var allPermissions = GetDefaultPermissionCatalog();
        var permissions = GetDefaultRolePreset(role);
        try
        {
            if (_settingsService != null)
            {
                var json = await _settingsService.GetAsync($"RolePermissions_{role}");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var overrides = JsonSerializer.Deserialize<Dictionary<string, bool>>(json)
                        ?? throw new InvalidOperationException("Política inválida.");
                    foreach (var entry in overrides) permissions[entry.Key] = entry.Value;
                }
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (load != _permissionLoadVersion || security != _securityVersion || SelectedPermissionRole.Trim().ToUpperInvariant() != role) return;
                foreach (var perm in allPermissions)
                {
                    perm.IsGranted = permissions.GetValueOrDefault(perm.Key);
                    perm.IsEditable = role != "ADMINISTRADOR" || !PermissionCatalog.AdministratorRecovery.Contains(perm.Key);
                    if (!perm.IsEditable) perm.Description += " Obligatorio para conservar el acceso administrativo y la recuperación.";
                    RolePermissions.Add(perm);
                }
                _loadedPermissionRole = role;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading role permissions");
            if (load == _permissionLoadVersion && security == _securityVersion)
                PermissionFeedbackMessage = "No se pudo leer la política. No se puede guardar hasta cargarla correctamente.";
        }
        finally { if (load == _permissionLoadVersion) IsPermissionLoading = false; }
    }

    [RelayCommand]
    private async Task SaveRolePermissionsAsync()
    {
        string role = SelectedPermissionRole.Trim().ToUpperInvariant();
        if (_settingsService == null || !IsAdminUnlocked || IsPermissionLoading || _loadedPermissionRole != role)
        { PermissionFeedbackMessage = "Desbloquee y cargue la política del rol antes de guardar."; return; }
        long version = _securityVersion;
        long load = _permissionLoadVersion;
        try
        {
            var dict = RolePermissions.ToDictionary(p => p.Key, p => p.IsGranted);
            string json = JsonSerializer.Serialize(dict);
            // The settings service authorizes and journals the committed change with the real actor.
            await _settingsService.SetAsync($"RolePermissions_{role}", json);
            if (version == _securityVersion && load == _permissionLoadVersion)
                PermissionFeedbackMessage = $"Permisos del rol '{role}' guardados.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving permissions for role {Role}", SelectedPermissionRole);
            if (version == _securityVersion && load == _permissionLoadVersion)
                PermissionFeedbackMessage = "No se guardaron los permisos. El administrador debe conservar acceso a usuarios, roles, configuración y recuperación.";
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
        new() { Key = "settings.security", Category = "8. Ajustes Globales & Sistema", Title = "Configuración de Seguridad & Respaldos", Description = "Permite realizar backups de base de datos y llaves de encriptación" },

        // Every backend permission must be represented, including commercial reconciliation.
        new() { Key = PermissionCatalog.PurchasesManage, Category = "9. Operaciones comerciales", Title = "Gestionar compras y pagos", Description = "Permite registrar compras, pagos y devoluciones a proveedor" },
        new() { Key = PermissionCatalog.SuppliersManage, Category = "9. Operaciones comerciales", Title = "Gestionar proveedores", Description = "Permite consultar y mantener el catálogo de proveedores" },
        new() { Key = PermissionCatalog.ExpensesManage, Category = "9. Operaciones comerciales", Title = "Gestionar gastos", Description = "Permite registrar y cancelar gastos con evidencia de recuperación" },
        new() { Key = PermissionCatalog.PromotionsManage, Category = "9. Operaciones comerciales", Title = "Gestionar promociones", Description = "Permite crear, modificar e inactivar promociones" },
        new() { Key = PermissionCatalog.WalletManage, Category = "9. Operaciones comerciales", Title = "Gestionar monederos", Description = "Permite crear, recargar y consultar monederos" },
        new() { Key = PermissionCatalog.PaymentsReconcile, Category = "9. Operaciones comerciales", Title = "Conciliar pagos externos", Description = "Permite verificar transferencias y reembolsos bancarios" },
        new() { Key = PermissionCatalog.CheckoutRecover, Category = "9. Operaciones comerciales", Title = "Supervisar cobros interrumpidos", Description = "Permite resolver intentos en la caja original, conservando al cajero original y registrando al aprobador" },
        new() { Key = PermissionCatalog.AttendanceManage, Category = "9. Operaciones comerciales", Title = "Revisar asistencias", Description = "Permite marcar asistencias inciertas para revisión, sin inventar horas trabajadas" },
        new() { Key = PermissionCatalog.ShiftNotesManage, Category = "9. Operaciones comerciales", Title = "Supervisar notas de caja", Description = "Permite corregir, resolver y cancelar notas de otros usuarios en esta caja, conservando revisiones" },
        new() { Key = PermissionCatalog.AccountCreditSettle, Category = "9. Operaciones comerciales", Title = "Liquidar saldos a favor", Description = "Permite aplicar créditos entre documentos o registrar reembolsos comprobados. Los bancarios también requieren conciliación" },
        new() { Key = PermissionCatalog.HistoryReconcile, Category = "9. Operaciones comerciales", Title = "Conciliar saldos históricos", Description = "Permite ajustar saldos actuales con evidencia, sin modificar documentos originales ni efectivo" }
    ];

    private static Dictionary<string, bool> GetDefaultRolePreset(string role)
    {
        string normalized = role.Trim().ToUpperInvariant() switch { "ADMIN" => "ADMINISTRADOR", var value => value };
        return PermissionCatalog.GetDefaultPermissions(normalized);
    }

    [RelayCommand]
    private Task SaveUserAsync() => SaveUserCoreAsync();

    [RelayCommand]
    private void RequestDeleteUser(UserDto user)
    {
        if (user == null || IsUserSaving) return;
        ++_securityVersion;

        if (user.Role.ToUpper() == "ADMINISTRADOR" || user.Role.ToUpper() == "ADMIN")
        {
            UserToDelete = user;
            IsConfirmingAdminDelete = true;
            AdminDeletePassword = string.Empty;
            FeedbackMessage = "Para desactivar un administrador, confirma tu propia contraseña de administrador.";
        }
        else
        {
            _ = ConfirmDeleteUserAsync(user);
        }
    }

    [RelayCommand]
    private void CancelAdminDelete()
    {
        ++_securityVersion;
        IsConfirmingAdminDelete = false;
        UserToDelete = null;
        AdminDeletePassword = string.Empty;
        FeedbackMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmAdminDeleteAsync()
    {
        if (UserToDelete == null || _userService == null) return;

        long version = _securityVersion;
        var target = UserToDelete;
        try
        {
            bool valid = await _userService.ConfirmCurrentAdministratorAsync(AdminDeletePassword);
            if (version != _securityVersion || UserToDelete != target) return;
            AdminDeletePassword = string.Empty;
            if (valid)
            {
                await ConfirmDeleteUserAsync(target);
                if (version == _securityVersion) CancelAdminDelete();
            }
            else FeedbackMessage = "Credencial incorrecta o temporalmente bloqueada. No se puede desactivar.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Administrative account deactivation verification failed");
            if (version == _securityVersion) { AdminDeletePassword = ""; FeedbackMessage = "No se pudo confirmar tu autorización."; }
        }
    }

    private async Task ConfirmDeleteUserAsync(UserDto user)
    {
        if (_userService == null || user == null) return;
        long version = _securityVersion;
        try
        {
            await _userService.DeleteAsync(user.Id);
            if (version != _securityVersion) return;
            await LoadAsync();
            if (version != _securityVersion) return;
            WeakReferenceMessenger.Default.Send(new UserDeletedMessage(user.Id));
            FeedbackMessage = $"Usuario {user.FullName} desactivado. El historial se conserva."
                + (_usersReadFailed ? " No se pudo actualizar la lista." : "");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UsuariosSettingsViewModel: error deleting user");
            if (version == _securityVersion) FeedbackMessage = "Error al desactivar usuario.";
        }
    }
}
