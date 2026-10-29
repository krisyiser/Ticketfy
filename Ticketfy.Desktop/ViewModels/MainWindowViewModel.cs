using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ticketfy.Data;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Audit;
using Ticketfy.Services.Auth;
using Ticketfy.Core.Services;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Navigation;
using Ticketfy.ViewModels.Shell;
using Ticketfy.Core.Models;
using Ticketfy.Core.Repositories;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;

namespace Ticketfy.ViewModels;

/// <summary>
/// Shell coordinator: composes sub-VMs for navigation, dialogs, update, lock, shift and tutorials.
/// Contains zero business logic — delegates to specialized sub-VMs.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    // ── Sub-VM Composition ──────────────────────────────────────────────────
    public NavigationService Navigation { get; }
    public DialogCoordinator Dialogs { get; }
    public AutoUpdateViewModel Update { get; }
    public LockScreenViewModel LockScreen { get; }
    public TutorialCoordinatorViewModel Tutorial { get; }
    public HardwareStatusViewModel Hardware { get; }
    public MaintenanceStatusViewModel Maintenance { get; }

    // ── Passthrough bindings for MainWindow.axaml compatibility ─────────────
    public ObservableObject ActiveViewModel => Navigation.ActiveViewModel;
    public ObservableObject? ActiveDialogViewModel => Dialogs.ActiveDialogViewModel;
    public bool IsDialogOverlayOpen => Dialogs.IsDialogOverlayOpen;
    public TutorialOverlayViewModel ActiveTutorialVm => Tutorial.ActiveTutorialVm;
    public bool IsLocked => LockScreen.IsLocked;
    public string UnlockPin
    {
        get => LockScreen.UnlockPin;
        set => LockScreen.UnlockPin = value;
    }
    public string UnlockErrorMessage => LockScreen.UnlockErrorMessage;
    public bool IsUpdateAvailable => Update.IsUpdateAvailable;
    public bool IsUpdateReady => Update.IsUpdateReady;
    public double UpdateProgress => Update.UpdateProgress;
    public string UpdateProgressText => Update.UpdateProgressText;
    public bool IsUpdateUpToDate => Update.IsUpdateUpToDate;
    public bool IsUpdateFailed => Update.IsUpdateFailed;
    public string UpdateErrorMessage => Update.UpdateErrorMessage;

    [ObservableProperty] private bool _isSplashScreenVisible = true;

    // ── Sidebar layout state ─────────────────────────────────────────────────
    [ObservableProperty] private string _sidebarDockPosition = "Left";
    [ObservableProperty] private double _sidebarWidth = 80;
    [ObservableProperty] private double _sidebarHeight = double.NaN;
    [ObservableProperty] private string _sidebarOrientation = "Vertical";

    public string CurrentAppVersion => Ticketfy.Core.Helpers.AppVersionHelper.DisplayVersion;
    public string FullAppVersionTitle => Ticketfy.Core.Helpers.AppVersionHelper.FullTitle;

    public event Action? ToggleFullscreenRequested;

    public MainWindowViewModel()
    {
        // 1. Initialize Dialog Coordinator FIRST to prevent null reference delegates
        Dialogs = new DialogCoordinator();
        Dialogs.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActiveDialogViewModel));
            OnPropertyChanged(nameof(IsDialogOverlayOpen));
        };

        // ── Database setup ───────────────────────────────────────────────────
        // Factories and workers belong to the application container, not to a window instance.
        var runtime = App.Current?.Services ?? throw new InvalidOperationException("Los servicios de la aplicación no se han inicializado.");
        var dbContextFactory = runtime.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var coOccurrenceQueue = runtime.GetRequiredService<CoOccurrenceQueue>();

        // ── Services ─────────────────────────────────────────────────────────
        var backupService = runtime.GetRequiredService<IBackupService>();
        var diagnosticService = runtime.GetRequiredService<IDiagnosticExportService>();
        var operationalTelemetry = runtime.GetRequiredService<IOperationalTelemetry>();
        Maintenance = new MaintenanceStatusViewModel(runtime.GetRequiredService<Ticketfy.Core.Operations.MaintenanceHealth>(),
            runtime.GetRequiredService<AuditVerificationCoordinator>(), diagnosticService);
        var permissionEngine = new UserRolePermissionEngine(new SettingsService(dbContextFactory));
        var sessionManager = new SessionManager();
        var quickAccess = new Ticketfy.Services.Security.QuickAccessSession();
        sessionManager.CashierChanged += user => { if (user is null) quickAccess.Clear(); };
        var operations = new Ticketfy.Services.CommercialOperationContext(sessionManager, permissionEngine,
            Environment.GetEnvironmentVariable("TICKETFY_REGISTER_ID") ?? "POS-" + Environment.MachineName);
        var settingsService = new SettingsService(dbContextFactory, operations);
        _settingsService = settingsService;
        var printerService = new EscPosPrinterService(settingsService);
        Hardware = new HardwareStatusViewModel(settingsService);
        var productService = new ProductService(dbContextFactory, operations);
        var customerService = new CustomerService(dbContextFactory, operations);
        var auditService = runtime.GetRequiredService<IAuditService>();
        var saleService = new SaleService(dbContextFactory, coOccurrenceQueue, auditService, operations);
        var promotionService = new PromotionService(dbContextFactory, operations);
        var giftcardService = new GiftcardService(dbContextFactory, operations);
        var supplierService = new SupplierService(dbContextFactory, operations);
        var purchaseService = new PurchaseService(dbContextFactory, operations);
        var expenseService = new ExpenseService(dbContextFactory, operations);
        Func<string> auditActor = () => sessionManager.CurrentCashier?.Id.ToString() ?? "SYSTEM";
        var userService = new UserService(dbContextFactory, auditService, auditActor, operations, quickAccess: quickAccess);
        var terminalService = new MercadoPagoTerminalService(new System.Net.Http.HttpClient(), settingsService, contextFactory: dbContextFactory,
            maintenance: runtime.GetRequiredService<Ticketfy.Core.Operations.MaintenanceHealth>(), operations: operations);
        var shiftNoteService = new ShiftNoteService(dbContextFactory, operations);
        var kitService = new ItemKitService(dbContextFactory, operations);
        var predictiveService = new PredictiveIntelligenceService(dbContextFactory, operations);
        var externalCatalogService = new ExternalCatalogService(new System.Net.Http.HttpClient());
        var performanceAnalyticsService = new PerformanceAnalyticsService(dbContextFactory, operations);
        var attendanceService = new AttendanceService(dbContextFactory, operations);
        var authService = new AuthService(userService, permissionEngine);
        var shiftService = new ShiftService(dbContextFactory, operations);
        var userRepository = new UserRepository(dbContextFactory, permissionEngine, auditService, auditActor, operations, quickAccess: quickAccess);

        var certificateGenerator = new CertificateGeneratorService();
        var printDispatcherService = new PrintDispatcherService(printerService, certificateGenerator, dbContextFactory, settingsService);
        var tutorialService = new TutorialService(settingsService);

        var securityService = new SecurityInterceptionService(userRepository);
        securityService.RequestSupervisorPinDialog += (title, callback) =>
        {
            SupervisorPinDialogViewModel? dialog = null;
            dialog = new SupervisorPinDialogViewModel(userRepository, title, (authorized, user) =>
            {
                bool current = Dialogs.TryCloseDialog(dialog);
                callback(current && authorized, current ? user : null);
            });
            Dialogs.ShowDialog(dialog);
        };

        var facturamaService =
            (App.Current?.Services?.GetService(typeof(IFacturamaService)) as IFacturamaService)
            ?? new UnavailableFacturamaService();
        var fiscalQueue = new FiscalIntegrationQueueService(dbContextFactory, facturamaService);
        if (LocalDevelopmentMode.FacturamaEnabled)
        {
            _ = new FiscalQueueWorker(fiscalQueue).RunAsync(default);
        }

        // ── Module ViewModels ─────────────────────────────────────────────────
        var posVm = new PosViewModel(productService, shiftNoteService, customerService,
            sessionManager, promotionService,
            predictiveService, externalCatalogService, loadOnCreate: false);
        var inventoryVm = new InventoryViewModel(productService, externalCatalogService, purchaseService, predictiveService, printerService,
            new InventorySnapshotService(dbContextFactory, operations), loadOnCreate: false);
        var customersVm = new CustomersViewModel(customerService, loadOnCreate: false);
        var historyVm = new HistoryViewModel(saleService, printerService, settingsService, shiftService, loadOnCreate: false);
        var promotionsVm = new PromotionsViewModel(promotionService, kitService, loadOnCreate: false);
        var fiscalVm = new FiscalViewModel(saleService, fiscalQueue, loadOnCreate: false);
        var cashierPerformanceVm = new CashierPerformanceViewModel(performanceAnalyticsService, attendanceService, loadOnCreate: false);
        async Task AuthorizeSettingsSection(string permission)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            await operations.RequireAsync(db, permission, false);
        }
        var settingsVm = new SettingsViewModel(userService, settingsService, backupService, auditService, auditActor,
            diagnosticService, operationalTelemetry, new RecoveryBundleService(dbContextFactory, operations), AuthorizeSettingsSection);
        var suppliersVm = new SuppliersViewModel(supplierService, purchaseService, productService, printerService, loadOnCreate: false);
        var expensesVm = new ExpensesViewModel(expenseService, loadOnCreate: false, shiftService: shiftService);
        var loginVm = new LoginViewModel(authService, sessionManager,
            new DialogService((vmObj) =>
            {
                if (vmObj is string str && str == "LOCK_SCREEN") { sessionManager.LockTerminal(); return Task.FromResult<object?>(null); }
                Dialogs.ShowDialog((vmObj as ObservableObject)!);
                return Task.FromResult<object?>(null);
            }, Dialogs.CloseDialog));

        // ── Navigation Service ───────────────────────────────────────────────
        Navigation = new NavigationService(loginVm, posVm, inventoryVm, customersVm, historyVm,
            promotionsVm, fiscalVm, settingsVm, suppliersVm, expensesVm, cashierPerformanceVm, operations, dbContextFactory, sessionManager);
        CommercialControlViewModel CreateCommercialControl() => new(productService, customerService, supplierService,
            purchaseService, shiftService, giftcardService, new CommercialReconciliationService(dbContextFactory, operations), dbContextFactory, operations, saleService, terminalService);
        Navigation.CommercialControl = CreateCommercialControl();
        Navigation.SessionInvalidated += () =>
        {
            Dialogs.CloseDialog();
            CloseSecondaryWindows();
            Navigation.CommercialControl = CreateCommercialControl();
        };
        sessionManager.LockStateChanged += locked =>
        {
            if (!locked) return;
            Dialogs.CloseDialog();
            CloseSecondaryWindows();
        };
        Navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationService.ActiveViewModel))
                OnPropertyChanged(nameof(ActiveViewModel));
        };

        Update = new AutoUpdateViewModel();
        Update.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsUpdateAvailable));
            OnPropertyChanged(nameof(IsUpdateReady));
            OnPropertyChanged(nameof(UpdateProgress));
            OnPropertyChanged(nameof(UpdateProgressText));
            OnPropertyChanged(nameof(IsUpdateUpToDate));
            OnPropertyChanged(nameof(IsUpdateFailed));
            OnPropertyChanged(nameof(UpdateErrorMessage));
        };

        LockScreen = new LockScreenViewModel(userRepository, sessionManager);
        LockScreen.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(UnlockPin));
            OnPropertyChanged(nameof(UnlockErrorMessage));
        };

        Tutorial = new TutorialCoordinatorViewModel(tutorialService);
        Tutorial.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ActiveTutorialVm));

        // ── ShiftCoordinator ─────────────────────────────────────────────────
        var shiftCoordinator = new ShiftCoordinatorViewModel(shiftService, sessionManager,
            printerService, backupService, attendanceService);
        shiftCoordinator.ShowDialogRequested += Dialogs.ShowDialog;
        shiftCoordinator.CloseDialogRequested += Dialogs.CloseDialog;
        shiftCoordinator.LogoutRequested += () => Navigation.GoToLogin();

        // ── DialogWirer ──────────────────────────────────────────────────────
        var wirer = new DialogWirer(Dialogs, productService, customerService, saleService,
            printerService, printDispatcherService, giftcardService, shiftService,
            sessionManager, userRepository, backupService, attendanceService, auditService,
            terminalService, kitService, shiftNoteService, new CommercialReconciliationService(dbContextFactory, operations), new CategoryService(dbContextFactory, operations));

        wirer.WirePosVm(posVm, inventoryVm, historyVm);
        wirer.WireInventoryVm(inventoryVm, posVm);
        wirer.WireCustomersVm(customersVm, posVm);
        wirer.WireHistoryVm(historyVm, posVm);
        wirer.WirePromotionsVm(promotionsVm, posVm, promotionService);
        wirer.WireExpensesVm(expensesVm);

        // Wirer handles promotionsVm event subscriptions cleanly via DialogWirer.

        // ── POS cashup shortcuts ──────────────────────────────────────────────
        posVm.OpenPartialCashupRequested += () => shiftCoordinator.OpenPartialCashup();
        posVm.OpenFinalCashupRequested += () => shiftCoordinator.OpenFinalCashup();

        // ── Fullscreen / Logout ───────────────────────────────────────────────
        posVm.ToggleFullscreenRequested += () => ToggleFullscreenRequested?.Invoke();
        posVm.LogoutRequested += () =>
        {
            Navigation.GoToLogin();
        };

        // ── Login post-action ─────────────────────────────────────────────────
        loginVm.LoginSuccessful += async () =>
        {
            var loginVersion = sessionManager.SessionVersion;
            try
            {
                var stage = await settingsService.GetInitialSetupStageAsync();
                if (loginVersion != sessionManager.SessionVersion) return;
                if (stage is InitialSetupStage.Business or InitialSetupStage.Staff)
                {
                    await settingsService.GetInitialBusinessAsync(); // Resuming requires the persisted administrator, never an automatic session.
                    if (loginVersion == sessionManager.SessionVersion)
                        BeginInitialSetup(userRepository, settingsService, sessionManager, stage);
                    return;
                }
                if (stage == InitialSetupStage.NotStarted) throw new InvalidOperationException("La cuenta inicial no está disponible.");
                Navigation.GoToPos();
                posVm.RegisterMessages();
                await posVm.LoadSessionAsync();
                if (loginVersion != sessionManager.SessionVersion) return;

                var shiftSuccess = await shiftCoordinator.ValidateShiftStatusAsync();
                if (shiftSuccess && loginVersion == sessionManager.SessionVersion)
                {
                    WeakReferenceMessenger.Default.Send(new Ticketfy.Core.Messages.FocusSearchMessage());
                    _ = Tutorial.TryStartSidebarTourAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Post-login routing failed");
                if (loginVersion != sessionManager.SessionVersion) return;
                Navigation.GoToLogin();
                loginVm.ErrorMessage = "No se pudo preparar la sesión. " + ex.Message;
            }
        };

        Tutorial.SidebarTourCompleted += () => _ = Tutorial.LaunchModuleTourAsync("Module.POS");
        Navigation.ModuleActivated += (key) => _ = Tutorial.LaunchModuleTourAsync(key);

        // ── Sidebar layout ────────────────────────────────────────────────────
        ThemeService.Instance.SidebarPositionChanged += pos =>
            Dispatcher.UIThread.Post(() => ApplySidebarLayout(pos));

        // ── Global messages ───────────────────────────────────────────────────
        WeakReferenceMessenger.Default.Register<ForceLogoutMessage>(this, (r, m) =>
        {
            long expectedVersion = m.SessionVersion ?? sessionManager.SessionVersion;
            Dispatcher.UIThread.Post(() =>
            {
                if (expectedVersion != sessionManager.SessionVersion) return;
                Navigation.GoToLogin(); Dialogs.CloseDialog();
            });
        });

        WeakReferenceMessenger.Default.Register<Ticketfy.Core.Messages.UserDeletedMessage>(this, (r, m) =>
        {
            if (sessionManager.CurrentCashier?.Id.ToString() == m.UserId)
                Dispatcher.UIThread.Post(() =>
                {
                    if (sessionManager.CurrentCashier?.Id.ToString() == m.UserId)
                        WeakReferenceMessenger.Default.Send(new ForceLogoutMessage(sessionManager.SessionVersion));
                });
        });

        _ = InitializeApplicationStateAsync(userRepository, settingsService, sessionManager);
    }

    // ── Commands delegated to sub-VMs ────────────────────────────────────────

    [RelayCommand]
    private async Task NavigateTo(string target)
        => await Navigation.NavigateTo(target);

    [RelayCommand]
    private async Task CheckForUpdates()
        => await Update.CheckForUpdatesCommand.ExecuteAsync(null);

    [RelayCommand]
    private void ApplyUpdateAndRestart()
        => Update.ApplyUpdateAndRestartCommand.Execute(null);

    [RelayCommand]
    private async Task UnlockTerminalAsync()
        => await LockScreen.UnlockTerminalCommand.ExecuteAsync(null);

    [RelayCommand]
    private void EndLockedSession()
    {
        if (!LockScreen.IsLocked) return;
        Dialogs.CloseDialog();
        Navigation.GoToLogin();
    }

    [RelayCommand]
    private void TriggerPosCheckout()
    {
        if (Navigation.IsAtPos)
            Navigation.PosVm.Cart.CheckoutCommand.Execute("Efectivo");
    }

    // ── Sidebar layout helper ─────────────────────────────────────────────────
    private void ApplySidebarLayout(string position)
    {
        switch (position)
        {
            case "Derecha":
                SidebarDockPosition = "Right"; SidebarWidth = 80; SidebarHeight = double.NaN; SidebarOrientation = "Vertical";
                break;
            case "Arriba (Top Bar)":
            case "Arriba":
            case "Arriba (Banner)":
            case "Barra Superior Flotante":
                SidebarDockPosition = "Top"; SidebarWidth = double.NaN; SidebarHeight = 64; SidebarOrientation = "Horizontal";
                break;
            case "Abajo (Bottom Bar)":
            case "Abajo":
            case "Abajo (Footer)":
                SidebarDockPosition = "Bottom"; SidebarWidth = double.NaN; SidebarHeight = 64; SidebarOrientation = "Horizontal";
                break;
            default:
                SidebarDockPosition = "Left"; SidebarWidth = 80; SidebarHeight = double.NaN; SidebarOrientation = "Vertical";
                break;
        }
    }

    // ── App startup routing ───────────────────────────────────────────────────
    private async Task InitializeApplicationStateAsync(IUserRepository userRepository,
        ISettingsService settingsService, ISessionManager sessionManager)
    {
        try
        {
            _ = DismissSplashScreenAsync();
            var appSettings = await settingsService.GetAppSettingsAsync();
            Ticketfy.Services.Settings.ThemeEngine.Instance.Apply(appSettings);

            var licenseService = new LicenseEnforcementService();
            if (licenseService.IsSystemLocked())
            {
                Navigation.GoToLicenseLocked(new LicenseLockedViewModel());
                return;
            }

            var stage = await settingsService.GetInitialSetupStageAsync();
            if (stage == InitialSetupStage.NotStarted)
                BeginInitialSetup(userRepository, settingsService, sessionManager, stage);
            else
            {
                Navigation.GoToLogin();
                if (stage is InitialSetupStage.Business or InitialSetupStage.Staff)
                    Navigation.LoginVm.InformationMessage = "Configuración pendiente. Entre con la cuenta administradora creada para continuar.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize application state");
            Navigation.GoToLogin();
            Navigation.LoginVm.ErrorMessage = "No se pudo verificar la configuración inicial. Conserve los datos y vuelva a iniciar la aplicación.";
            _ = DismissSplashScreenAsync();
        }
    }

    private void BeginInitialSetup(IUserRepository users, ISettingsService settings, ISessionManager session, InitialSetupStage stage)
    {
        var dialogs = new DialogService(_ => Task.FromResult<object?>(null), () => { });
        var license = new LicenseEnforcementService();
        Action welcome = null!, admin = null!, business = null!, staff = null!;
        Func<Task> finish = async () =>
        {
            var version = session.SessionVersion;
            await settings.CompleteInitialSetupAsync();
            if (version == session.SessionVersion) Navigation.GoToLogin();
        };
        welcome = () => Navigation.GoTo(new WelcomeLicenseViewModel(license, admin));
        admin = () => Navigation.GoTo(new FirstTimeSetupViewModel(users, dialogs, business, welcome, session.StartSession));
        business = () => Navigation.GoTo(new SetupBusinessDataViewModel(settings, staff,
            stage == InitialSetupStage.NotStarted ? admin : Navigation.GoToLogin));
        staff = () => Navigation.GoTo(new SetupAdditionalUsersViewModel(users, finish, business));
        if (stage == InitialSetupStage.NotStarted) welcome();
        else if (stage == InitialSetupStage.Staff) staff();
        else business();
    }

    private async Task DismissSplashScreenAsync()
    {
        await Task.Delay(3000);
        IsSplashScreenVisible = false;
    }

    public async Task<int> GetIdleTimeoutMinutesAsync()
    {
        var val = await _settingsService.GetAsync("IdleTimeoutMinutes");
        return int.TryParse(val, out var m) && m > 0 ? m : 5;
    }

    public void TriggerAutoLock() => LockScreen.TriggerAutoLock();

    private static void CloseSecondaryWindows()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var window in desktop.Windows.Where(window => window != desktop.MainWindow).ToArray())
            window.Close();
    }
}
