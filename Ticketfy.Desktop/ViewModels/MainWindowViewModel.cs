using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
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
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;

namespace Ticketfy.ViewModels;

/// <summary>
/// Shell coordinator: composes sub-VMs for navigation, dialogs, update, lock, shift and tutorials.
/// Contains zero business logic — delegates to specialized sub-VMs.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    // ── Sub-VM Composition ──────────────────────────────────────────────────
    public NavigationService Navigation { get; }
    public DialogCoordinator Dialogs { get; }
    public AutoUpdateViewModel Update { get; }
    public LockScreenViewModel LockScreen { get; }
    public TutorialCoordinatorViewModel Tutorial { get; }
    public HardwareStatusViewModel Hardware { get; }

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
        AppDataPaths.EnsureRequiredDirectories();

        string securePassword = SecurityManager.GetMasterKey();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={securePassword};Cache=Shared;Mode=ReadWriteCreate;")
            .AddInterceptors(new Ticketfy.Data.Interceptors.SlowQueryInterceptor())
            .Options;

        var db = new AppDbContext(options);
        var dbContextFactory = new Ticketfy.Data.AppDbContextFactoryImpl<AppDbContext>(options);

        var auditOptions = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite($"Data Source={AppDataPaths.AuditDatabaseFile};")
            .Options;
        var auditContextFactory = new Ticketfy.Data.AppDbContextFactoryImpl<AuditDbContext>(auditOptions);

        // ── Background workers ───────────────────────────────────────────────
        _ = new AuditArchivingWorker(auditContextFactory).StartAsync(default);
        var coOccurrenceQueue = new CoOccurrenceQueue();
        _ = new CoOccurrenceWorker(coOccurrenceQueue, dbContextFactory).StartAsync(default);
        _ = new AlertBackgroundWorker(dbContextFactory).StartAsync(default);

        // ── Services ─────────────────────────────────────────────────────────
        var backupService =
            (App.Current?.Services?.GetService(typeof(IBackupService)) as IBackupService)
            ?? new BackupService();
        var diagnosticService =
            (App.Current?.Services?.GetService(typeof(IDiagnosticExportService)) as IDiagnosticExportService)
            ?? new DiagnosticExportService();
        var operationalTelemetry =
            (App.Current?.Services?.GetService(typeof(IOperationalTelemetry)) as IOperationalTelemetry)
            ?? new LocalOperationalTelemetry();
        var permissionEngine = new UserRolePermissionEngine(new SettingsService(dbContextFactory));
        var sessionManager = new SessionManager();
        var operations = new Ticketfy.Services.CommercialOperationContext(sessionManager, permissionEngine,
            Environment.GetEnvironmentVariable("TICKETFY_REGISTER_ID") ?? "POS-" + Environment.MachineName);
        var settingsService = new SettingsService(dbContextFactory, operations);
        var printerService = new EscPosPrinterService(settingsService);
        Hardware = new HardwareStatusViewModel(settingsService);
        var productService = new ProductService(dbContextFactory, operations);
        var customerService = new CustomerService(dbContextFactory, operations);
        var auditService = new AuditService(auditContextFactory);
        var saleService = new SaleService(dbContextFactory, coOccurrenceQueue, auditService, operations);
        var promotionService = new PromotionService(dbContextFactory, operations);
        var giftcardService = new GiftcardService(db, operations);
        var supplierService = new SupplierService(dbContextFactory, operations);
        var purchaseService = new PurchaseService(dbContextFactory, operations);
        var expenseService = new ExpenseService(dbContextFactory, operations);
        Func<string> auditActor = () => sessionManager.CurrentCashier?.Id.ToString() ?? "SYSTEM";
        var userService = new UserService(db, auditService, auditActor, operations);
        var terminalService = new MercadoPagoTerminalService(new System.Net.Http.HttpClient(), settingsService, contextFactory: dbContextFactory);
        var shiftNoteService = new ShiftNoteService(db);
        var kitService = new ItemKitService(db, operations);
        var predictiveService = new PredictiveIntelligenceService(dbContextFactory);
        var externalCatalogService = new ExternalCatalogService(new System.Net.Http.HttpClient());
        var performanceAnalyticsService = new PerformanceAnalyticsService(db, operations);
        var attendanceService = new AttendanceService(db);
        var authService = new AuthService(userService, permissionEngine);
        var shiftService = new ShiftService(dbContextFactory, operations);
        var userRepository = new UserRepository(db, permissionEngine, auditService, auditActor, operations);

        var certificateGenerator = new CertificateGeneratorService();
        var printDispatcherService = new PrintDispatcherService(printerService, certificateGenerator, dbContextFactory, settingsService);
        var tutorialService = new TutorialService(settingsService);

        var securityService = new SecurityInterceptionService(userRepository);
        securityService.RequestSupervisorPinDialog += (title, callback) =>
        {
            var dialog = new SupervisorPinDialogViewModel(userRepository, title, callback);
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
        var posVm = new PosViewModel(productService, db, shiftNoteService, kitService, customerService,
            sessionManager, userRepository, promotionService, auditService, attendanceService,
            predictiveService, externalCatalogService);
        var inventoryVm = new InventoryViewModel(productService, externalCatalogService, purchaseService, predictiveService, printerService);
        var customersVm = new CustomersViewModel(customerService);
        var historyVm = new HistoryViewModel(saleService, printerService, db, settingsService, shiftService);
        var promotionsVm = new PromotionsViewModel(promotionService, kitService);
        var fiscalVm = new FiscalViewModel(saleService, fiscalQueue);
        var cashierPerformanceVm = new CashierPerformanceViewModel(performanceAnalyticsService, attendanceService);
        var settingsVm = new SettingsViewModel(userService, settingsService, backupService, auditService, auditActor,
            diagnosticService, operationalTelemetry);
        var suppliersVm = new SuppliersViewModel(supplierService, purchaseService, productService, printerService);
        var expensesVm = new ExpensesViewModel(expenseService, shiftService);
        var loginVm = new LoginViewModel(authService, sessionManager,
            new DialogService((vmObj) =>
            {
                if (vmObj is string str && str == "LOCK_SCREEN") { sessionManager.LockTerminal(); return Task.FromResult<object?>(null); }
                Dialogs.ShowDialog((vmObj as ObservableObject)!);
                return Task.FromResult<object?>(null);
            }, Dialogs.CloseDialog));

        // ── Navigation Service ───────────────────────────────────────────────
        Navigation = new NavigationService(loginVm, posVm, inventoryVm, customersVm, historyVm,
            promotionsVm, fiscalVm, settingsVm, suppliersVm, expensesVm, cashierPerformanceVm, operations, dbContextFactory);
        Navigation.CommercialControl = new CommercialControlViewModel(productService, customerService, supplierService,
            purchaseService, shiftService, giftcardService, new CommercialReconciliationService(dbContextFactory, operations), dbContextFactory, operations, saleService);
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
        var shiftCoordinator = new ShiftCoordinatorViewModel(db, shiftService, sessionManager,
            printerService, backupService, attendanceService);
        shiftCoordinator.ShowDialogRequested += Dialogs.ShowDialog;
        shiftCoordinator.CloseDialogRequested += Dialogs.CloseDialog;
        shiftCoordinator.LogoutRequested += () => Navigation.GoToLogin();

        // ── DialogWirer ──────────────────────────────────────────────────────
        var wirer = new DialogWirer(Dialogs, productService, customerService, saleService,
            printerService, printDispatcherService, giftcardService, db, shiftService,
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
            Navigation.GoToPos();
            posVm.RegisterMessages();
            _ = posVm.LoadProductsAsync();

            var shiftSuccess = await shiftCoordinator.ValidateShiftStatusAsync();
            if (shiftSuccess)
            {
                WeakReferenceMessenger.Default.Send(new Ticketfy.Core.Messages.FocusSearchMessage());
                _ = Tutorial.TryStartSidebarTourAsync();
            }
        };

        Tutorial.SidebarTourCompleted += () => _ = Tutorial.LaunchModuleTourAsync("Module.POS");
        Navigation.ModuleActivated += (key) => _ = Tutorial.LaunchModuleTourAsync(key);

        // ── Sidebar layout ────────────────────────────────────────────────────
        ThemeService.Instance.SidebarPositionChanged += pos =>
            Dispatcher.UIThread.Post(() => ApplySidebarLayout(pos));

        // ── Global messages ───────────────────────────────────────────────────
        WeakReferenceMessenger.Default.Register<ForceLogoutMessage>(this, (r, m) =>
            Dispatcher.UIThread.Post(() => { Navigation.GoToLogin(); Dialogs.CloseDialog(); }));

        WeakReferenceMessenger.Default.Register<Ticketfy.Core.Messages.UserDeletedMessage>(this, (r, m) =>
        {
            if (sessionManager.CurrentCashier?.Id.ToString() == m.UserId)
                Dispatcher.UIThread.Post(() =>
                    WeakReferenceMessenger.Default.Send(new ForceLogoutMessage()));
        });

        _ = InitializeApplicationStateAsync(userRepository, settingsService, db, sessionManager, permissionEngine);
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
        ISettingsService settingsService, AppDbContext db, ISessionManager sessionManager, IUserRolePermissionEngine permissionEngine)
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

            string? setupDone = await settingsService.GetAsync("IsOnboardingCompleted");
            bool hasUsers = await userRepository.HasAnyUsersAsync();
            if (!hasUsers)
            {
                var dialogService = new DialogService((vm) => Task.FromResult<object?>(null), () => { });
                Action finishSetup = async () =>
                {
                    await settingsService.SetAsync("IsOnboardingCompleted", "true");
                    sessionManager.ClearSession();
                    Navigation.GoToLogin();
                };

                Action goToWelcome = null!;
                Action goToAdminAccount = null!;
                Action goToBusinessData = null!;
                Action goToAdditionalUsers = null!;

                goToWelcome = () =>
                    Navigation.GoTo(new WelcomeLicenseViewModel(licenseService, goToAdminAccount));

                goToAdminAccount = () =>
                    Navigation.GoTo(new FirstTimeSetupViewModel(userRepository, dialogService, goToBusinessData, goToWelcome,
                        user => sessionManager.StartSession(permissionEngine.MapToModel(user))));

                goToBusinessData = () =>
                    Navigation.GoTo(new SetupBusinessDataViewModel(settingsService, goToAdditionalUsers, goToAdminAccount));

                goToAdditionalUsers = () =>
                    Navigation.GoTo(new SetupAdditionalUsersViewModel(userRepository, finishSetup, goToBusinessData));

                // OOBE Onboarding Step 1: Friendly Welcome & License Activation Screen
                goToWelcome();
            }
            else
            {
                Navigation.GoToLogin();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize application state");
            _ = DismissSplashScreenAsync();
        }
    }

    private async Task DismissSplashScreenAsync()
    {
        await Task.Delay(3000);
        IsSplashScreenVisible = false;
    }

    public async Task<int> GetIdleTimeoutMinutesAsync()
    {
        var settingsSvc = new SettingsService(
            new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={SecurityManager.GetMasterKey()};")
                .Options));
        var val = await settingsSvc.GetAsync("IdleTimeoutMinutes");
        return int.TryParse(val, out var m) && m > 0 ? m : 5;
    }

    public void TriggerAutoLock() => LockScreen.TriggerAutoLock();
}
