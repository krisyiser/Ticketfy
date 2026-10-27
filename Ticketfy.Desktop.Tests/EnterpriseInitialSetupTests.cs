using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Navigation;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseInitialSetupTests : IAsyncLifetime
{
    private const string Password = "Caminos de cobre y lluvia 7842!";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SessionManager _session = new();
    private DbContextOptions<AppDbContext> _options = null!;
    private CommercialOperationContext _actor = null!;
    private AppDbContext Db() => new(_options);
    private SettingsService Settings => new(new TestDbContextFactory(_options), _actor);
    private UserRepository Users => new(new TestDbContextFactory(_options), operations: _actor);
    private static InitialBusinessProfile Profile => new("Laboratorio", "5544332211", "lab@example.invalid", "Dirección sintética");
    private static UserAccountDraft Administrator => new() { Id = Guid.NewGuid(), FullName = "Responsable", Username = "laboratorio", Role = "ADMINISTRADOR", Password = Password, Pin = "7834", IsActive = true };
    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        await using var db = Db(); await db.Database.MigrateAsync();
        _actor = new(_session, new UserRolePermissionEngine(), "LAB-SETUP");
    }
    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstAdministrator_AndDurableSetupStageCommitTogether(bool adminMethod)
    {
        Assert.Equal(InitialSetupStage.NotStarted, await Settings.GetInitialSetupStageAsync());
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_admin BEFORE INSERT ON transaction_journal
            WHEN NEW.event_type IN ('ADMIN_SAVED', 'USER_CREATED') BEGIN SELECT RAISE(ABORT, 'synthetic'); END;
            """);
        var draft = Administrator;
        await Assert.ThrowsAsync<DbUpdateException>(() => adminMethod ? Users.SaveOrUpdateAdminUserAsync(draft) : Users.CreateUserAsync(draft));
        Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.Settings.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_admin;");
        var saved = adminMethod ? await Users.SaveOrUpdateAdminUserAsync(draft) : await Users.CreateUserAsync(draft);
        Assert.Equal(InitialSetupStage.Business, await Settings.GetInitialSetupStageAsync());
        Assert.Equal("false", await Settings.GetAsync("IsOnboardingCompleted"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Settings.GetInitialBusinessAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Users.SaveOrUpdateAdminUserAsync(draft));
        _session.StartSession(saved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settings.CompleteInitialSetupAsync());
        await Settings.SaveInitialBusinessAsync(Profile);
        await Settings.SaveInitialBusinessAsync(Profile);
        Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EventType == "INITIAL_BUSINESS_SAVED"));
        Assert.Equal(InitialSetupStage.Staff, await Settings.GetInitialSetupStageAsync());
        _session.ClearSession(); // Represents exiting before the last step. A new repository must authenticate.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Settings.CompleteInitialSetupAsync());
        var authenticated = await Users.VerifyPasswordAsync(draft.Username, Password);
        Assert.NotNull(authenticated); _session.StartSession(authenticated);
        await Users.CreateUserAsync(new() { Id = Guid.NewGuid(), FullName = "Cajero", Username = "cajero", Password = Password, Pin = "7359", Role = "CAJERO", IsActive = true });
        await Settings.CompleteInitialSetupAsync(); await Settings.CompleteInitialSetupAsync();
        Assert.Equal(InitialSetupStage.Completed, await Settings.GetInitialSetupStageAsync());
        Assert.Equal("true", await Settings.GetAsync("IsOnboardingCompleted"));
        Assert.Equal(2, await db.Users.CountAsync()); Assert.Empty(await db.Shifts.ToListAsync());
        Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EventType == "INITIAL_SETUP_COMPLETED"));
        Assert.All(await db.TransactionJournal.Where(j => j.EventType.StartsWith("INITIAL_")).ToListAsync(), j => Assert.Equal(saved.Id.ToString(), j.CashierUserId));
        Assert.Equal(Profile, await Settings.GetInitialBusinessAsync());
    }

    [Fact]
    public async Task Finalization_FailurePreservesPendingStageAndRetryDoesNotDuplicateJournal()
    {
        _session.StartSession(await Users.SaveOrUpdateAdminUserAsync(Administrator));
        await Settings.SaveInitialBusinessAsync(Profile);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_finish BEFORE INSERT ON transaction_journal
            WHEN NEW.event_type = 'INITIAL_SETUP_COMPLETED' BEGIN SELECT RAISE(ABORT, 'synthetic'); END;
            """);
        var vm = new SetupAdditionalUsersViewModel(Users, () => Settings.CompleteInitialSetupAsync());
        await vm.SkipCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.ErrorMessage); Assert.False(vm.IsWorking);
        Assert.Equal(InitialSetupStage.Staff, await Settings.GetInitialSetupStageAsync());
        Assert.Equal("false", await Settings.GetAsync("IsOnboardingCompleted"));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_finish;");
        await vm.SkipCommand.ExecuteAsync(null); await vm.SkipCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErrorMessage);
        Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EventType == "INITIAL_SETUP_COMPLETED"));
    }

    [Fact]
    public async Task BusinessSave_MergesOnlyContactFieldsAndProtectsStageFromGenericSettings()
    {
        _session.StartSession(await Users.SaveOrUpdateAdminUserAsync(Administrator));
        var settings = await Settings.GetAppSettingsAsync();
        settings.System.AutoLockTimeoutMinutes = 3; settings.Hardware.PrinterPort = "LAB-ONLY";
        settings.Company.TimeZoneId = "America/Cancun"; settings.Company.Rfc = "SYNTHETIC";
        await Settings.SaveAppSettingsAsync(settings);
        await Settings.SaveInitialBusinessAsync(Profile with { Name = " Laboratorio " });
        var actual = await Settings.GetAppSettingsAsync();
        Assert.Equal(3, actual.System.AutoLockTimeoutMinutes); Assert.Equal("LAB-ONLY", actual.Hardware.PrinterPort);
        Assert.Equal("America/Cancun", actual.Company.TimeZoneId); Assert.Equal("SYNTHETIC", actual.Company.Rfc);
        Assert.Equal(Profile, await Settings.GetInitialBusinessAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settings.SetAsync("InitialSetup.Stage", "Completed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settings.SetAsync("IsOnboardingCompleted", "true"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("phone")]
    [InlineData("email")]
    [InlineData("control")]
    [InlineData("length")]
    public async Task InitialProfile_BackendValidationRejectsInvalidTextWithoutChanges(string defect)
    {
        _session.StartSession(await Users.SaveOrUpdateAdminUserAsync(Administrator));
        var invalid = defect switch
        {
            "name" => Profile with { Name = " " },
            "phone" => Profile with { Phone = "abcdef1234" },
            "email" => Profile with { Email = "Name <lab@example.invalid>" },
            "control" => Profile with { Name = "Invalid\u001b" },
            _ => Profile with { Address = new string('a', 513) }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settings.SaveInitialBusinessAsync(invalid));
        Assert.Equal(InitialSetupStage.Business, await Settings.GetInitialSetupStageAsync());
        Assert.Null(await Settings.GetAsync("BusinessName"));
    }

    [Fact]
    public async Task SetupState_RejectsUnknownStateAndDoesNotReopenLegacyAccountCreation()
    {
        await using var db = Db();
        db.Users.Add(new() { Username = "legacy", FullName = "Historical", IsActive = true }); await db.SaveChangesAsync();
        Assert.Equal(InitialSetupStage.ExistingInstallation, await Settings.GetInitialSetupStageAsync());
        db.Settings.Add(new() { Key = "InitialSetup.Stage", Value = "unrecognized" }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Settings.GetInitialSetupStageAsync());
    }

    [Fact]
    public async Task Cashier_CannotReadBusinessSetupOrFinishIt()
    {
        _session.StartSession(await Users.SaveOrUpdateAdminUserAsync(Administrator));
        var cashier = await Users.CreateUserAsync(new() { Id = Guid.NewGuid(), Username = "cashier", FullName = "Cashier", Role = "CAJERO", Password = Password, Pin = "3298", IsActive = true });
        _session.StartSession(cashier);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Settings.GetInitialBusinessAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Settings.SaveInitialBusinessAsync(Profile));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Settings.CompleteInitialSetupAsync());
    }

    [AvaloniaFact]
    public async Task InitialAdministratorUi_LateResultCannotStartSessionAfterLeavingAndDoubleClickIsBlocked()
    {
        var release = new TaskCompletionSource();
        var repository = new DeferredSetupUsers(Users, release.Task);
        int starts = 0, advances = 0;
        var vm = new FirstTimeSetupViewModel(repository, null!, () => advances++, onAdminCreated: _ => starts++);
        await vm.InitializationTask;
        vm.AdminFullName = "Responsable"; vm.AdminUsername = "laboratorio"; vm.AdminPassword = Password;
        vm.AdminPin1 = "7"; vm.AdminPin2 = "8"; vm.AdminPin3 = "3"; vm.AdminPin4 = "4";
        var window = new Window { Content = new FirstTimeSetupView { DataContext = vm } }; window.Show();
        try
        {
            var creating = vm.CreateAdminAccountCommand.ExecuteAsync(null);
            Assert.True(vm.IsSaving); Assert.False(vm.CreateAdminAccountCommand.CanExecute(null));
            Assert.False(vm.GoBackCommand.CanExecute(null));
            await vm.CreateAdminAccountCommand.ExecuteAsync(null);
            vm.Deactivate(); release.SetResult(); await creating;
            Assert.Equal(0, starts); Assert.Equal(0, advances); Assert.Empty(vm.AdminPassword); Assert.Empty(vm.AdminPin);
            Assert.Equal(InitialSetupStage.Business, await Settings.GetInitialSetupStageAsync());
            await using var db = Db(); Assert.Single(await db.Users.ToListAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BusinessUi_LoadFailureIsVisibleAndLateReadsOrSavesCannotRestoreDepartedStep()
    {
        var read = new TaskCompletionSource<InitialBusinessProfile>();
        var service = new SetupSettings { Read = () => read.Task };
        int advances = 0;
        var vm = new SetupBusinessDataViewModel(service, () => advances++);
        var window = new Window { Content = new SetupBusinessDataView { DataContext = vm } }; window.Show();
        try
        {
            Assert.False(vm.CanEdit); Assert.False(vm.SaveAndContinueCommand.CanExecute(null));
            read.SetException(new IOException("synthetic")); await vm.InitializationTask;
            Assert.NotEmpty(vm.ErrorMessage); Assert.False(vm.CanEdit);
            service.Read = () => Task.FromResult(Profile);
            await vm.ReloadCommand.ExecuteAsync(null); Assert.True(vm.CanEdit);
            var saved = new TaskCompletionSource(); service.Save = () => saved.Task;
            var saving = vm.SaveAndContinueCommand.ExecuteAsync(null);
            Assert.False(vm.GoBackCommand.CanExecute(null));
            vm.Deactivate(); saved.SetResult(); await saving;
            Assert.Equal(0, advances); Assert.Empty(vm.BusinessName); Assert.Empty(vm.ErrorMessage);
            read = new(); service.Read = () => read.Task;
            var stale = new SetupBusinessDataViewModel(service, () => advances++);
            stale.Deactivate(); read.SetResult(Profile); await stale.InitializationTask;
            Assert.Empty(stale.BusinessName); Assert.False(stale.CanEdit);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SetupNavigation_CannotLeaveForCommercialModulesAndLogoutClearsDraft()
    {
        var login = new LoginViewModel(new Ticketfy.Services.Auth.AuthService(new UserService(new TestDbContextFactory(_options))), _session, null!);
        var nav = new NavigationService(login, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, _actor, new TestDbContextFactory(_options), _session);
        _session.StartSession(await Users.SaveOrUpdateAdminUserAsync(Administrator));
        Assert.Same(login, nav.ActiveViewModel); // Identity validation alone does not route to POS.
        var business = new SetupBusinessDataViewModel(Settings, () => { }); await business.InitializationTask;
        nav.GoTo(business); await nav.NavigateTo("settings"); Assert.Same(business, nav.ActiveViewModel);
        var staff = new SetupAdditionalUsersViewModel(Users, () => Task.CompletedTask) { Password = Password, Username = "draft" };
        nav.GoTo(staff); Assert.False(business.CanEdit);
        await nav.NavigateTo("pos"); Assert.Same(staff, nav.ActiveViewModel);
        nav.GoToLogin(); Assert.Empty(staff.Password); Assert.Empty(staff.Username);
        Assert.Same(login, nav.ActiveViewModel); Assert.Null(_session.CurrentCashier);
    }

    private sealed class SetupSettings : ISettingsService
    {
        public Func<Task<InitialBusinessProfile>> Read { get; set; } = () => Task.FromResult(Profile);
        public Func<Task> Save { get; set; } = () => Task.CompletedTask;
        public Task<InitialBusinessProfile> GetInitialBusinessAsync() => Read();
        public Task SaveInitialBusinessAsync(InitialBusinessProfile profile) => Save();
        public Task<string?> GetAsync(string key) => throw new NotSupportedException();
        public Task SetAsync(string key, string value) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> GetAllAsync() => throw new NotSupportedException();
        public Task<AppSettings> GetAppSettingsAsync() => throw new NotSupportedException();
        public Task SaveAppSettingsAsync(AppSettings settings) => throw new NotSupportedException();
    }

    private sealed class DeferredSetupUsers(IUserRepository inner, Task release) : IUserRepository
    {
        public async Task<Ticketfy.Core.Models.UserModel> SaveOrUpdateAdminUserAsync(UserAccountDraft draft)
        { var saved = await inner.SaveOrUpdateAdminUserAsync(draft); await release; return saved; }
        public Task<Ticketfy.Data.Dtos.UserDto?> GetAdminProfileAsync() => inner.GetAdminProfileAsync();
        public Task<List<Ticketfy.Core.Models.UserModel>> GetActiveUsersAsync() => inner.GetActiveUsersAsync();
        public Task<Ticketfy.Core.Models.UserModel?> ValidatePinAsync(string username, string pin4Digits) => inner.ValidatePinAsync(username, pin4Digits);
        public Task<Ticketfy.Core.Models.UserModel?> VerifyPasswordAsync(string username, string password, bool supervisorOnly = false, Func<bool>? isRequestCurrent = null) => inner.VerifyPasswordAsync(username, password, supervisorOnly, isRequestCurrent);
        public Task<bool> HasAnyUsersAsync() => inner.HasAnyUsersAsync();
        public Task<Ticketfy.Core.Models.UserModel> CreateUserAsync(UserAccountDraft draft) => inner.CreateUserAsync(draft);
    }
}
