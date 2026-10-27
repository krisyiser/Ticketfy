using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ticketfy.Core.Services;
using Ticketfy.Core.Models;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Settings;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseModuleActivationTests
{
    [AvaloniaFact]
    public void DeferredModules_ConstructionAndFirstIdentityDoNotQueryBusinessServices()
    {
        var probes = new List<Probe>();
        T Service<T>() where T : class { var proxy = Probe.Create<T>(); probes.Add(Probe.Of(proxy)); return proxy; }
        var products = Service<IProductService>(); var customers = Service<ICustomerService>();
        var sales = Service<ISaleService>(); var purchases = Service<IPurchaseService>();
        var settings = Service<ISettingsService>(); var printer = Service<IEscPosPrinterService>();
        var session = new SessionManager();
        var pos = new PosViewModel(products, Service<IShiftNoteService>(), customers, session,
            Service<IPromotionService>(), Service<IPredictiveIntelligenceService>(), Service<IExternalCatalogService>(), loadOnCreate: false);
        _ = new InventoryViewModel(products, Service<IExternalCatalogService>(), purchases, loadOnCreate: false);
        _ = new CustomersViewModel(customers, loadOnCreate: false);
        _ = new HistoryViewModel(sales, printer, settings, Service<IShiftService>(), loadOnCreate: false);
        _ = new PromotionsViewModel(Service<IPromotionService>(), Service<IItemKitService>(), loadOnCreate: false);
        _ = new FiscalViewModel(sales, Service<IFiscalIntegrationQueueService>(), loadOnCreate: false);
        _ = new CashierPerformanceViewModel(Service<IPerformanceAnalyticsService>(), Service<IAttendanceService>(), loadOnCreate: false);
        _ = new SuppliersViewModel(Service<ISupplierService>(), purchases, products, printer, loadOnCreate: false);
        _ = new ExpensesViewModel(Service<IExpenseService>(), loadOnCreate: false);
        _ = new SettingsViewModel(Service<IUserService>(), settings);
        session.StartSession(new UserModel { FullName = "Administrador de configuración" });
        Dispatcher.UIThread.RunJobs();
        Assert.All(probes, p => Assert.Empty(p.Calls));
        Assert.Empty(pos.Cart.Customers);
    }
    [AvaloniaFact]
    public void ClearingSettingsSession_DoesNotQueryReconstructedSections()
    {
        var settings = Probe.Create<ISettingsService>();
        var users = Probe.Create<IUserService>();
        var vm = new SettingsViewModel(users, settings);
        Dispatcher.UIThread.RunJobs();
        Probe.Of(settings).Calls.Clear(); Probe.Of(users).Calls.Clear();
        vm.ClearSessionData();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Probe.Of(settings).Calls); Assert.Empty(Probe.Of(users).Calls);
    }

    [AvaloniaFact]
    public void PosHeader_WithoutCashier_DoesNotQueryShiftNotes()
    {
        var notes = Probe.Create<IShiftNoteService>();
        var vm = new PosHeaderViewModel(new SessionManager(), notes, new Ticketfy.Core.State.CartStateStore());
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Probe.Of(notes).Calls);
        Assert.Empty(vm.ActiveShiftNotes);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShiftNotes_LateReadFromSameUserEarlierSessionCannotReplaceOrClearNewNotes(bool failure)
    {
        var notes = Probe.Create<IShiftNoteService>();
        var pending = new TaskCompletionSource<List<ShiftNoteDto>>();
        Probe.Of(notes).Handler = (_, _) => pending.Task;
        var session = new SessionManager(); var user = new UserModel { FullName = "Laboratorio" };
        session.StartSession(user);
        var vm = new PosHeaderViewModel(session, notes, new Ticketfy.Core.State.CartStateStore(), loadOnCreate: false);
        var oldRead = vm.LoadActiveShiftNotesAsync();
        session.SwitchCashier(user);
        Probe.Of(notes).Handler = (_, _) => Task.FromResult(new List<ShiftNoteDto> { new("CURRENT", "Laboratorio", "", "Actual", "General", false) });
        await vm.LoadActiveShiftNotesAsync();
        if (failure) pending.SetException(new IOException("synthetic"));
        else pending.SetResult([new("OLD", "Laboratorio", "", "Anterior", "General", false)]);
        await oldRead;
        Assert.Equal("CURRENT", Assert.Single(vm.ActiveShiftNotes).Id);
        session.ClearSession(); await vm.LoadActiveShiftNotesAsync();
        Assert.Empty(vm.ActiveShiftNotes); Assert.Equal(2, Probe.Of(notes).Calls.Count);
    }

    [AvaloniaFact]
    public async Task Settings_OnlySelectedAuthorizedSectionLoadsAndFailedReadsCannotBeSaved()
    {
        var settings = Probe.Create<ISettingsService>(); var users = Probe.Create<IUserService>();
        bool hardwareAllowed = false;
        var permissions = new List<string>();
        Task Authorize(string permission)
        {
            permissions.Add(permission);
            return permission == "settings.hardware" && !hardwareAllowed
                ? Task.FromException(new UnauthorizedAccessException()) : Task.CompletedTask;
        }
        var vm = new SettingsViewModel(users, settings, authorizeSection: Authorize);
        Assert.Empty(Probe.Of(settings).Calls); Assert.Empty(Probe.Of(users).Calls);
        await vm.LoadCurrentSectionAsync();
        Assert.True(vm.CanEditSection); Assert.Single(Probe.Of(settings).Calls);
        var hardware = vm.Sections.Single(s => s.ViewModel is HardwareSettingsViewModel);
        await vm.SelectSectionCommand.ExecuteAsync(hardware);
        Assert.False(vm.CanEditSection); Assert.NotEmpty(vm.FeedbackMessage);
        Assert.Single(Probe.Of(settings).Calls); Assert.Empty(Probe.Of(users).Calls);
        hardwareAllowed = true;
        Probe.Of(settings).Handler = (_, _) => Task.FromException<AppSettings>(new IOException("synthetic"));
        await vm.SelectSectionCommand.ExecuteAsync(hardware);
        await vm.SaveAllSettingsCommand.ExecuteAsync(null);
        Assert.False(vm.CanEditSection); Assert.All(Probe.Of(settings).Calls, c => Assert.StartsWith("Get", c));
        Probe.Of(settings).Handler = null;
        await vm.SelectSectionCommand.ExecuteAsync(hardware);
        Assert.True(vm.CanEditSection); Assert.Same(hardware.ViewModel, vm.CurrentSectionViewModel);
        Assert.Equal(4, permissions.Count(p => p == "settings.hardware")); // Denied, failed read, then authorization before and after the successful read.
        Assert.Empty(Probe.Of(users).Calls);
    }

    [AvaloniaTheory]
    [InlineData("business")]
    [InlineData("hardware")]
    [InlineData("security")]
    public async Task Settings_LateSectionReadsCannotRepopulateDiscardedSession(string section)
    {
        var settings = Probe.Create<ISettingsService>();
        var pending = new TaskCompletionSource<AppSettings>();
        Probe.Of(settings).Handler = (method, _) => method.Name == nameof(ISettingsService.GetAppSettingsAsync) ? pending.Task : Task.FromResult(new Dictionary<string, string>());
        var vm = new SettingsViewModel(Probe.Create<IUserService>(), settings, authorizeSection: _ => Task.CompletedTask);
        var item = vm.Sections.Single(s => section switch { "business" => s.ViewModel is EmpresaSettingsViewModel, "hardware" => s.ViewModel is HardwareSettingsViewModel, _ => s.ViewModel is SeguridadSettingsViewModel });
        var loading = vm.SelectSectionCommand.ExecuteAsync(item);
        Assert.True(vm.IsSectionLoading);
        vm.ClearSessionData();
        var data = new AppSettings(); data.Company.CommercialName = "OLD-SESSION"; data.Hardware.PrinterPort = "OLD-SESSION";
        data.System.BackupPath = "OLD-SESSION";
        pending.SetResult(data); await loading;
        Assert.False(vm.CanEditSection); Assert.False(vm.IsSectionLoading); Assert.Empty(vm.FeedbackMessage);
        Assert.DoesNotContain(item, vm.Sections);
        if (item.ViewModel is EmpresaSettingsViewModel b) { Assert.False(b.HasLoadedData); Assert.Empty(b.EmpresaNombreComercial); }
        if (item.ViewModel is HardwareSettingsViewModel h) { Assert.False(h.HasLoadedData); Assert.Empty(h.State.Hardware.PrinterPort); }
        if (item.ViewModel is SeguridadSettingsViewModel s) { Assert.False(s.HasLoadedData); Assert.NotEqual("OLD-SESSION", s.State.System.BackupPath); }
    }

    [AvaloniaFact]
    public async Task Settings_RevokedPermissionDuringRefreshHidesPreviouslyVisibleSection()
    {
        bool revoke = false; int checks = 0;
        Task Authorize(string _) => revoke && ++checks == 2 ? Task.FromException(new UnauthorizedAccessException()) : Task.CompletedTask;
        var vm = new SettingsViewModel(Probe.Create<IUserService>(), Probe.Create<ISettingsService>(), authorizeSection: Authorize);
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 900 }; window.Show();
        try
        {
            var business = vm.Sections.Single(s => s.ViewModel is EmpresaSettingsViewModel);
            await vm.SelectSectionCommand.ExecuteAsync(business); Dispatcher.UIThread.RunJobs();
            var host = view.FindControl<TransitioningContentControl>("SettingsSectionHost")!;
            Assert.True(vm.CanEditSection); Assert.True(host.IsVisible);
            revoke = true;
            await vm.RefreshSectionCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Assert.False(vm.CanEditSection); Assert.NotEmpty(vm.FeedbackMessage);
            Assert.False(host.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BusinessSettings_DoNotMigrateOrOverwriteFiscalCredentialsWhileFacturamaIsDisabled()
    {
        Assert.False(Ticketfy.Core.Configuration.LocalDevelopmentMode.FacturamaEnabled);
        var settings = Probe.Create<ISettingsService>(); var written = new List<string>();
        Probe.Of(settings).Handler = (method, arguments) =>
        {
            if (method.Name == nameof(ISettingsService.GetAppSettingsAsync)) return Task.FromResult(new AppSettings());
            if (method.Name == nameof(ISettingsService.GetAllAsync)) return Task.FromResult(new Dictionary<string, string>
            { ["FacturamaApiUser"] = "synthetic-user", ["FacturamaApiPassword"] = "synthetic-secret" });
            if (method.Name == nameof(ISettingsService.SetAsync)) { written.Add((string)arguments![0]!); return Task.CompletedTask; }
            if (method.Name == nameof(ISettingsService.SaveBusinessSettingsAsync)) { written.AddRange(((IReadOnlyDictionary<string, string>)arguments![0]!).Keys); return Task.CompletedTask; }
            throw new InvalidOperationException("Unexpected service call");
        };
        var vm = new EmpresaSettingsViewModel(settings, loadOnCreate: false);
        await vm.LoadAsync();
        Assert.True(vm.HasLoadedData); Assert.Empty(vm.FacturamaApiUser); Assert.Empty(vm.FacturamaApiPassword); Assert.Empty(written);
        Assert.False(vm.IsFiscalIntegrationEnabled); Assert.Equal("NO VERIFICADO", vm.CsdValidityStatus);
        vm.EmpresaNombreComercial = "Laboratorio";
        Assert.True(await vm.SaveAsync());
        Assert.NotEmpty(written); Assert.DoesNotContain(written, key => key.StartsWith("Facturama", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task BusinessSettings_TabsNavigateAndRefreshRemovesClearedContactData()
    {
        var settings = Probe.Create<ISettingsService>(); var data = new AppSettings();
        data.Company.CommercialName = "Laboratorio"; data.Company.Email = "old@example.invalid";
        Probe.Of(settings).Handler = (method, _) => method.Name == nameof(ISettingsService.GetAppSettingsAsync)
            ? Task.FromResult(data) : Task.FromResult(new Dictionary<string, string> { ["EmpresaEstado"] = "Ciudad de México" });
        var vm = new EmpresaSettingsViewModel(settings, loadOnCreate: false);
        await vm.LoadAsync(); Assert.Equal("old@example.invalid", vm.EmpresaEmailContacto);
        Assert.Equal("Ciudad de México", vm.EmpresaEstado);
        var view = new Ticketfy.Views.Settings.EmpresaSettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 900 }; window.Show();
        try
        {
            vm.SelectEmpresaSubTabCommand.Execute("fiscal"); Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<UserControl>("BusinessGeneralTab")!.IsVisible);
            Assert.True(view.FindControl<UserControl>("BusinessFiscalTab")!.IsVisible);
            data = new AppSettings(); data.Company.CommercialName = "Laboratorio";
            await vm.LoadAsync(); Assert.Empty(vm.EmpresaEmailContacto);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("business")]
    [InlineData("hardware")]
    [InlineData("security")]
    [InlineData("visual")]
    public async Task Settings_OverlappingReadsCannotReplaceLatestSnapshot(string section)
    {
        var settings = Probe.Create<ISettingsService>(); var pending = new TaskCompletionSource<AppSettings>();
        int reads = 0; var latest = new AppSettings(); latest.Company.CommercialName = "LATEST";
        Probe.Of(settings).Handler = (method, _) => method.Name == nameof(ISettingsService.GetAppSettingsAsync)
            ? (++reads == 1 ? pending.Task : Task.FromResult(latest)) : Task.FromResult(new Dictionary<string, string>());
        object vm = section switch
        {
            "business" => new EmpresaSettingsViewModel(settings, loadOnCreate: false),
            "hardware" => new HardwareSettingsViewModel(settings, loadOnCreate: false),
            "security" => new SeguridadSettingsViewModel(settings, loadOnCreate: false),
            _ => new PersonalizacionSettingsViewModel(settings, loadOnCreate: false)
        };
        Task Load() => vm switch
        {
            EmpresaSettingsViewModel b => b.LoadAsync(),
            HardwareSettingsViewModel h => h.LoadAsync(),
            SeguridadSettingsViewModel s => s.LoadAsync(),
            PersonalizacionSettingsViewModel p => p.LoadAsync(),
            _ => throw new InvalidOperationException()
        };
        var oldRead = Load(); await Load();
        var old = new AppSettings(); old.Company.CommercialName = "OLD"; pending.SetResult(old); await oldRead;
        var name = vm switch
        {
            EmpresaSettingsViewModel b => b.EmpresaNombreComercial,
            HardwareSettingsViewModel h => h.State.Company.CommercialName,
            SeguridadSettingsViewModel s => s.State.Company.CommercialName,
            PersonalizacionSettingsViewModel p => p.State.Company.CommercialName,
            _ => ""
        };
        Assert.Equal("LATEST", name);
    }

    public class Probe : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
        public static T Create<T>() where T : class => Create<T, Probe>();
        public static Probe Of(object proxy) => (Probe)proxy;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal)) return null;
            Calls.Add(method.Name);
            if (Handler is not null) return Handler(method, arguments);
            var type = method.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = type.GetGenericArguments()[0];
                object? result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                if (resultType.GetConstructor(Type.EmptyTypes) is not null) result = Activator.CreateInstance(resultType);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result]);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
