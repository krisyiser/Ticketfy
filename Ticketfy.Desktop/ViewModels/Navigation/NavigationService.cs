using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Navigation;

/// <summary>
/// Routes navigation between top-level modules.
/// Owns module activation side-effects (data loading, tutorial launch).
/// Decoupled from dialog management and shell state.
/// </summary>
public partial class NavigationService : ObservableObject
{
    private readonly Ticketfy.Services.CommercialOperationContext? _operations;
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Ticketfy.Data.AppDbContext>? _factory;
    private readonly Ticketfy.Core.Services.ISessionManager? _session;
    private long _navigationVersion;
    public Task SessionActivationTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private string _authorizationError = string.Empty;
    private readonly PosViewModel _posVm;
    private readonly InventoryViewModel _inventoryVm;
    private readonly CustomersViewModel _customersVm;
    private readonly HistoryViewModel _historyVm;
    private readonly PromotionsViewModel _promotionsVm;
    private readonly FiscalViewModel _fiscalVm;
    private readonly SettingsViewModel _settingsVm;
    private readonly SuppliersViewModel _suppliersVm;
    private readonly ExpensesViewModel _expensesVm;
    private readonly CashierPerformanceViewModel _cashierPerformanceVm;
    private readonly LoginViewModel _loginVm;

    [ObservableProperty] private ObservableObject _activeViewModel;

    /// <summary>Fires with the module key after navigation, so TutorialCoordinator can launch tours.</summary>
    public event Action<string>? ModuleActivated;
    public event Action? SessionInvalidated;

    public NavigationService(
        LoginViewModel loginVm,
        PosViewModel posVm,
        InventoryViewModel inventoryVm,
        CustomersViewModel customersVm,
        HistoryViewModel historyVm,
        PromotionsViewModel promotionsVm,
        FiscalViewModel fiscalVm,
        SettingsViewModel settingsVm,
        SuppliersViewModel suppliersVm,
        ExpensesViewModel expensesVm,
        CashierPerformanceViewModel cashierPerformanceVm,
        Ticketfy.Services.CommercialOperationContext? operations = null,
        Microsoft.EntityFrameworkCore.IDbContextFactory<Ticketfy.Data.AppDbContext>? factory = null,
        Ticketfy.Core.Services.ISessionManager? session = null)
    {
        _loginVm = loginVm;
        _operations = operations;
        _factory = factory;
        _session = session;
        _posVm = posVm;
        _inventoryVm = inventoryVm;
        _customersVm = customersVm;
        _historyVm = historyVm;
        _promotionsVm = promotionsVm;
        _fiscalVm = fiscalVm;
        _settingsVm = settingsVm;
        _suppliersVm = suppliersVm;
        _expensesVm = expensesVm;
        _cashierPerformanceVm = cashierPerformanceVm;
        _activeViewModel = loginVm;
        if (settingsVm is not null) settingsVm.CloseRequested += () => NavigateTo("pos");
        if (session is not null)
        {
            session.CashierChanged += user =>
            {
                InvalidateSession();
                // Onboarding has its own route. A normal identity change must not retain
                // an administrative module, even while its old request is still finishing.
                if (user is null) ActiveViewModel = _loginVm;
                else if (ActiveViewModel != _loginVm && ActiveViewModel is not (WelcomeLicenseViewModel or FirstTimeSetupViewModel
                    or SetupBusinessDataViewModel or SetupAdditionalUsersViewModel or LicenseLockedViewModel))
                {
                    ActiveViewModel = _posVm;
                    SessionActivationTask = ActivatePosAfterIdentityChangeAsync(_navigationVersion);
                }
            };
            session.LockStateChanged += _ => ++_navigationVersion;
        }
    }

    public bool IsAtLogin => ActiveViewModel == _loginVm;
    private async Task ActivatePosAfterIdentityChangeAsync(long version)
    {
        if (_posVm is null || _factory is null || _operations is null) return;
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            await _operations.RequireAsync(db, "pos.checkout", false);
            if (version != _navigationVersion) return;
            await _posVm.LoadSessionAsync();
        }
        catch (Exception)
        {
            if (version == _navigationVersion) AuthorizationError = "No se pudo cargar el POS para la sesión actual. Vuelva a abrirlo.";
        }
    }
    partial void OnActiveViewModelChanging(ObservableObject value)
    {
        if (ActiveViewModel is HistoryViewModel history) history.Deactivate();
        if (ActiveViewModel is FirstTimeSetupViewModel admin) admin.Deactivate();
        if (ActiveViewModel is SetupBusinessDataViewModel business) business.Deactivate();
        if (ActiveViewModel is SetupAdditionalUsersViewModel staff) staff.Deactivate();
    }
    public bool IsAtPos => ActiveViewModel == _posVm;
    public PosViewModel PosVm => _posVm;
    public InventoryViewModel InventoryVm => _inventoryVm;
    public CustomersViewModel CustomersVm => _customersVm;
    public HistoryViewModel HistoryVm => _historyVm;
    public PromotionsViewModel PromotionsVm => _promotionsVm;
    public SettingsViewModel SettingsVm => _settingsVm;
    public CommercialControlViewModel? CommercialControl { get; set; }
    public SuppliersViewModel SuppliersVm => _suppliersVm;
    public LoginViewModel LoginVm => _loginVm;

    public void GoToLogin()
    {
        ++_navigationVersion;
        _loginVm.EndSession();
        if (_session is null) InvalidateSession();
        AuthorizationError = string.Empty;
        ActiveViewModel = _loginVm;
    }

    private void InvalidateSession()
    {
        ++_navigationVersion;
        _inventoryVm?.ClearSessionData();
        _customersVm?.ClearSessionData();
        _historyVm?.ClearSessionData();
        _promotionsVm?.ClearSessionData();
        _fiscalVm?.ClearSessionData();
        _settingsVm?.ClearSessionData();
        _suppliersVm?.ClearSessionData();
        _expensesVm?.ClearSessionData();
        _cashierPerformanceVm?.ClearSessionData();
        CommercialControl?.ClearDirectoryData();
        AuthorizationError = string.Empty;
        SessionInvalidated?.Invoke();
    }

    public void GoToPos() { ++_navigationVersion; ActiveViewModel = _posVm; }

    public void GoToLicenseLocked(LicenseLockedViewModel vm) { ++_navigationVersion; ActiveViewModel = vm; }
    public void GoTo(ObservableObject vm) { ++_navigationVersion; ActiveViewModel = vm; }

    [RelayCommand]
    public async Task NavigateTo(string target)
    {
        // Block navigation during login, dialogs, or locked states
        if (_session?.IsTerminalLocked == true || ActiveViewModel == _loginVm ||
            ActiveViewModel is LicenseLockedViewModel ||
            ActiveViewModel is WelcomeLicenseViewModel ||
            ActiveViewModel is FirstTimeSetupViewModel or SetupBusinessDataViewModel or SetupAdditionalUsersViewModel)
        {
            return;
        }

        var version = ++_navigationVersion;
        string key = target.ToLowerInvariant();
        string permission = key switch
        {
            "pos" or "commercial" => "pos.checkout",
            "inventory" => "inventory.view",
            "customers" => "customers.view",
            "suppliers" or "proveedores" => "suppliers.manage",
            "expenses" or "gastos" => "expenses.manage",
            "promotions" => "promotions.manage",
            "settings" => "settings.business",
            _ => "reports.view"
        };
        try
        {
            if (_factory is null || _operations is null) throw new UnauthorizedAccessException("Sesión requerida.");
            using var db = await _factory.CreateDbContextAsync();
            await _operations.RequireAsync(db, permission, false);
            if (version != _navigationVersion) return;
            var module = ResolveModule(key);
            // Load before exposing the module. Neither a late authorization nor a late
            // read may navigate away from login or restore another user's cached screen.
            await LoadModuleAsync(module, version);
            if (version != _navigationVersion) return;
            await _operations.RequireAsync(db, permission, false);
            if (version != _navigationVersion) return;
            AuthorizationError = string.Empty;
            ActiveViewModel = module;
            var tutorial = module switch
            {
                PosViewModel => "Module.POS",
                InventoryViewModel => "Module.Inventory",
                CustomersViewModel => "Module.Customers",
                HistoryViewModel => "Module.History",
                PromotionsViewModel => "Module.Promotions",
                SuppliersViewModel => "Module.Suppliers",
                ExpensesViewModel => "Module.Expenses",
                SettingsViewModel => "Module.Settings",
                _ => null
            };
            if (tutorial is not null) ModuleActivated?.Invoke(tutorial);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (version == _navigationVersion) AuthorizationError = ex.Message;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Module navigation failed");
            if (version == _navigationVersion) AuthorizationError = "No se pudo abrir el módulo. Vuelva a intentar.";
        }
    }

    private ObservableObject ResolveModule(string target) => target switch
    {
        "pos" => _posVm,
        "inventory" => _inventoryVm,
        "customers" => _customersVm,
        "suppliers" => _suppliersVm,
        "proveedores" => _suppliersVm,
        "expenses" => _expensesVm,
        "gastos" => _expensesVm,
        "commercial" => CommercialControl ?? (ObservableObject)_expensesVm,
        "history" => _historyVm,
        "promotions" => _promotionsVm,
        "fiscal" => _fiscalVm,
        "settings" => _settingsVm,
        "performance" => _cashierPerformanceVm,
        "rendimiento" => _cashierPerformanceVm,
        "cashierperformance" => _cashierPerformanceVm,
        _ => _posVm
    };

    private async Task LoadModuleAsync(ObservableObject module, long version)
    {
        switch (module)
        {
            case PosViewModel vm: await vm.LoadSessionAsync(); break;
            case InventoryViewModel vm: await vm.LoadProductsAsync(); break;
            case CustomersViewModel vm: await vm.LoadCustomersAsync(); break;
            case HistoryViewModel vm:
                await vm.LoadSavedCommissionAsync();
                if (version != _navigationVersion) return;
                await vm.LoadSalesAsync();
                if (version != _navigationVersion) return;
                await vm.LoadCashierPerformanceAsync();
                break;
            case PromotionsViewModel vm: await vm.LoadPromotionsAsync(); break;
            case FiscalViewModel vm: await vm.LoadInvoicesCommand.ExecuteAsync(null); break;
            case SuppliersViewModel vm: await vm.LoadDataAsync(); break;
            case ExpensesViewModel vm: await vm.LoadExpensesAsync(); break;
            case SettingsViewModel vm: await vm.LoadCurrentSectionAsync(); break;
            case CashierPerformanceViewModel vm: await vm.LoadReportsAsync(); break;
            case CommercialControlViewModel vm: await vm.RefreshAsync(); break;
        }
    }
}
