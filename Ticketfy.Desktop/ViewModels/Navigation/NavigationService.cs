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
        Microsoft.EntityFrameworkCore.IDbContextFactory<Ticketfy.Data.AppDbContext>? factory = null)
    {
        _loginVm = loginVm;
        _operations = operations;
        _factory = factory;
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
    }

    public bool IsAtLogin => ActiveViewModel == _loginVm;
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
        _loginVm.Username = string.Empty;
        _loginVm.Password = string.Empty;
        _loginVm.ErrorMessage = string.Empty;
        ActiveViewModel = _loginVm;
    }

    public void GoToPos() => ActiveViewModel = _posVm;

    public void GoToLicenseLocked(LicenseLockedViewModel vm) => ActiveViewModel = vm;
    public void GoTo(ObservableObject vm) => ActiveViewModel = vm;

    [RelayCommand]
    public async Task NavigateTo(string target)
    {
        // Block navigation during login, dialogs, or locked states
        if (ActiveViewModel == _loginVm ||
            ActiveViewModel is LicenseLockedViewModel ||
            ActiveViewModel is WelcomeLicenseViewModel ||
            ActiveViewModel is FirstTimeSetupViewModel)
        {
            return;
        }

        string permission = target.ToLowerInvariant() switch
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
            AuthorizationError = string.Empty;
        }
        catch (UnauthorizedAccessException ex) { AuthorizationError = ex.Message; return; }

        ActiveViewModel = target.ToLower() switch
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

        await ActivateCurrentModuleAsync();
        if (ActiveViewModel == CommercialControl && CommercialControl is not null) await CommercialControl.RefreshAsync();
    }

    private async Task ActivateCurrentModuleAsync()
    {
        if (ActiveViewModel == _posVm)
        {
            _ = _posVm.LoadProductsAsync();
            ModuleActivated?.Invoke("Module.POS");
        }
        else if (ActiveViewModel == _inventoryVm)
        {
            _ = _inventoryVm.LoadProductsAsync();
            ModuleActivated?.Invoke("Module.Inventory");
        }
        else if (ActiveViewModel == _customersVm)
        {
            _ = _customersVm.LoadCustomersAsync();
            ModuleActivated?.Invoke("Module.Customers");
        }
        else if (ActiveViewModel == _historyVm)
        {
            _ = _historyVm.LoadSalesAsync();
            ModuleActivated?.Invoke("Module.History");
        }
        else if (ActiveViewModel == _promotionsVm)
        {
            _ = _promotionsVm.LoadPromotionsAsync();
            ModuleActivated?.Invoke("Module.Promotions");
        }
        else if (ActiveViewModel == _fiscalVm)
        {
            _ = _fiscalVm.LoadInvoicesCommand.ExecuteAsync(null);
        }
        else if (ActiveViewModel == _suppliersVm)
        {
            _ = _suppliersVm.LoadDataAsync();
            ModuleActivated?.Invoke("Module.Suppliers");
        }
        else if (ActiveViewModel == _expensesVm)
        {
            _ = _expensesVm.LoadExpensesAsync();
            ModuleActivated?.Invoke("Module.Expenses");
        }
        else if (ActiveViewModel == _settingsVm)
        {
            _ = _settingsVm.LoadUsersAsync();
            ModuleActivated?.Invoke("Module.Settings");
        }
        else if (ActiveViewModel == _cashierPerformanceVm)
        {
            _ = _cashierPerformanceVm.LoadReportsAsync();
        }

        await Task.CompletedTask;
    }
}
