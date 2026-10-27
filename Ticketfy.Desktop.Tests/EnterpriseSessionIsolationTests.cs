using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Models;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;
using Ticketfy.Core.State;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Auth;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Navigation;
using Ticketfy.ViewModels.Shell;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task LockedAdministratorSession_RejectsOtherCashierPinAndAllowsOnlyOwner()
    {
        await using var db = Db();
        var admin = await db.Users.SingleAsync();
        admin.PinCode = SecurityManager.HashPin("8642");
        admin.PasswordHash = SecurityManager.HashPassword("Synthetic-owner-secret");
        db.Users.Add(new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = "other-cashier",
            FullName = "Synthetic cashier",
            RoleString = "CAJERO",
            IsActive = true,
            PinCode = SecurityManager.HashPin("1357")
        });
        await db.SaveChangesAsync();
        var quickAccess = new QuickAccessSession();
        Assert.NotNull(await new UserService(db, quickAccess: quickAccess).AuthenticateAsync(admin.Username, "Synthetic-owner-secret"));
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(admin));
        session.LockTerminal();
        using var model = new LockScreenViewModel(new UserRepository(db, quickAccess: quickAccess), session) { UsePassword = false, UnlockPin = "1357" };
        await model.UnlockTerminalCommand.ExecuteAsync(null);
        Assert.True(session.IsTerminalLocked);
        Assert.Equal(admin.Id, session.CurrentCashier!.Id);
        Assert.Empty(model.UnlockPin);
        var operations = new CommercialOperationContext(session, new UserRolePermissionEngine(), "TEST-REGISTER");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.RequireAsync(db, "inventory.view", false));
        Assert.True(model.UsePassword); // Failed quick access offers the primary credential.
        model.UsePassword = false;
        model.UnlockPin = "8642";
        await model.UnlockTerminalCommand.ExecuteAsync(null);
        Assert.False(session.IsTerminalLocked);
        Assert.Equal(admin.Id.ToString(), (await operations.RequireAsync(db, "inventory.view", false)).UserId);
        admin.IsActive = false;
        await db.SaveChangesAsync();
        session.LockTerminal();
        model.UnlockPin = "8642";
        await model.UnlockTerminalCommand.ExecuteAsync(null);
        Assert.True(session.IsTerminalLocked);
    }

    [Fact]
    public async Task PinValidationReturningAfterSessionChange_CannotUnlockNewSession()
    {
        var owner = new UserModel { Username = "synthetic-owner" };
        var session = new SessionManager();
        session.StartSession(owner);
        session.LockTerminal();
        var result = new TaskCompletionSource<UserModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new DeferredPinRepository(result.Task);
        using var model = new LockScreenViewModel(repository, session) { UsePassword = false, UnlockPin = "1234" };
        var pending = model.UnlockTerminalCommand.ExecuteAsync(null);
        session.ClearSession();
        session.StartSession(owner); // Even the same identity represents a different authentication session.
        session.LockTerminal();
        result.SetResult(owner);
        await pending;
        Assert.Equal(owner.Username, repository.RequestedUsername);
        Assert.True(session.IsTerminalLocked);
        Assert.False(model.LastUnlockSucceeded);
        Assert.Empty(model.UnlockErrorMessage); // Discard feedback from the previous authentication session too.
        Assert.Empty(model.UnlockPin);
    }

    [Fact]
    public async Task LogoutNavigation_RevokesServiceAuthorizationAndClearsCredentialsAndLock()
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var operations = new CommercialOperationContext(session, new UserRolePermissionEngine(), "TEST-REGISTER");
        Assert.NotNull(await operations.RequireAsync(db, "inventory.view", false));
        session.LockTerminal();
        var auth = new AuthService(new UserService(db));
        var login = new LoginViewModel(auth, session, null!) { Username = "do not retain", Password = "synthetic-secret", HintMessage = "hint", IsHintVisible = true };
        var navigation = new NavigationService(login, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        navigation.GoToLogin();
        Assert.Null(session.CurrentCashier);
        Assert.False(session.IsTerminalLocked);
        Assert.False(auth.IsAuthenticated);
        Assert.Null(auth.CurrentUser);
        Assert.Empty(login.Username);
        Assert.Empty(login.Password);
        Assert.Empty(login.HintMessage);
        Assert.False(login.IsHintVisible);
        Assert.True(navigation.IsAtLogin);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.RequireAsync(db, "inventory.view", false));
    }

    [AvaloniaFact]
    public async Task CustomerSearch_IsBoundedFindsBeyondFirstPageAndLogoutClearsSelectionAndTicket()
    {
        await using var db = Db();
        for (var i = 0; i < 110; i++) db.Customers.Add(new CustomerEntity { Id = $"customer-{i:D3}", Name = $"Synthetic {i:D3}", CustomerCode = $"CODE-{i:D3}" });
        await db.SaveChangesAsync();
        var customerService = new CustomerService(db, operations: _actor);
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        using var pos = new PosViewModel(ProductsService(), customerService: customerService, sessionManager: session, externalCatalogService: new NoNetworkCatalog());
        await pos.Cart.LoadCustomersAsync();
        Assert.Equal(101, pos.Cart.Customers.Count); // Bounded results plus public customer.
        pos.Cart.CustomerSearch = "CODE-109";
        await pos.Cart.SearchCustomersCommand.ExecuteAsync(null);
        pos.Cart.SelectedCustomer = Assert.Single(pos.Cart.Customers, c => c.Id == "customer-109");
        pos.Cart.CartState.AddItem(new CartItemDto("P", "Synthetic", 10m, 1), 20);
        session.ClearSession();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Empty(pos.Cart.Customers);
        Assert.Null(pos.Cart.SelectedCustomer);
        Assert.Empty(pos.Cart.CartState.Items);
        Assert.Empty(pos.Cart.CustomerSearch);
    }

    [AvaloniaFact]
    public async Task CustomerQueryReturningAfterLogout_DoesNotRepopulatePreviousSession()
    {
        var pending = new TaskCompletionSource<List<CustomerDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new CartViewModel(new CartStateStore(), null!, new DeferredCustomers(pending.Task));
        var load = model.LoadCustomersAsync();
        model.ClearSessionData();
        pending.SetResult([new CustomerDto("private", "Synthetic customer", "", "", "", 0m, 0m, 0d, 0m, "")]);
        await load;
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Empty(model.Customers);
        Assert.Null(model.SelectedCustomer);
    }

    private sealed class DeferredPinRepository(Task<UserModel?> pending) : IUserRepository
    {
        public string? RequestedUsername { get; private set; }
        public Task<UserModel?> ValidatePinAsync(string username, string pin4Digits) { RequestedUsername = username; return pending; }
        public Task<UserModel?> VerifyPasswordAsync(string username, string password, bool supervisorOnly = false, Func<bool>? isRequestCurrent = null)
        { RequestedUsername = username; return pending; }
        public Task<List<UserModel>> GetActiveUsersAsync() => throw new NotSupportedException();
        public Task<bool> HasAnyUsersAsync() => throw new NotSupportedException();
        public Task<UserModel> CreateUserAsync(UserAccountDraft user) => throw new NotSupportedException();
        public Task<UserModel> SaveOrUpdateAdminUserAsync(UserAccountDraft user) => throw new NotSupportedException();
        public Task<Ticketfy.Data.Dtos.UserDto?> GetAdminProfileAsync() => throw new NotSupportedException();
    }

    [Fact]
    public async Task PermissionDecisionReturningAfterLogout_CannotAuthorizeOperation()
    {
        await using var db = Db();
        var session = new SessionManager();
        session.StartSession(new UserRolePermissionEngine().MapToModel(await db.Users.SingleAsync()));
        var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new CommercialOperationContext(session, new DeferredPermissions(permission.Task), "TEST-REGISTER");
        var pending = operations.RequireAsync(db, "inventory.view", false);
        session.ClearSession();
        permission.SetResult(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
    }

    [Fact]
    public async Task PinRepositoryFailure_LeavesSessionLockedAndDoesNotExposeException()
    {
        var session = new SessionManager();
        session.StartSession(new UserModel { Username = "synthetic" });
        session.LockTerminal();
        using var model = new LockScreenViewModel(new DeferredPinRepository(Task.FromException<UserModel?>(new InvalidOperationException("synthetic-secret"))), session)
        { UsePassword = false, UnlockPin = "1234" };
        await model.UnlockTerminalCommand.ExecuteAsync(null);
        Assert.True(session.IsTerminalLocked);
        Assert.NotEmpty(model.UnlockErrorMessage);
        Assert.DoesNotContain("synthetic-secret", model.UnlockErrorMessage);
        Assert.Empty(model.UnlockPin);
    }

    private sealed class DeferredPermissions(Task<bool> pending) : IUserRolePermissionEngine
    {
        private readonly UserRolePermissionEngine _inner = new();
        public string NormalizeRoleName(string? rawRole) => _inner.NormalizeRoleName(rawRole);
        public UserModel MapToModel(UserEntity entity) => _inner.MapToModel(entity);
        public UserModel MapDtoToModel(UserDto dto, string? pinCode = null) => _inner.MapDtoToModel(dto, pinCode);
        public Task<bool> HasPermissionAsync(string roleName, string permissionKey) => pending;
        public Task<Dictionary<string, bool>> GetRolePermissionsAsync(string roleName) => _inner.GetRolePermissionsAsync(roleName);
        public bool IsAdminOrManager(string roleName) => _inner.IsAdminOrManager(roleName);
    }

    private sealed class DeferredCustomers(Task<List<CustomerDto>> pending) : ICustomerService
    {
        public Func<int, LedgerReadCut?, Task<AccountStatementPageDto>>? StatementRead { get; set; }
        public Task<AccountStatementPageDto> GetStatementPageAsync(string customerId, int page = 0, int size = 100, LedgerReadCut? cut = null)
            => StatementRead?.Invoke(page, cut) ?? throw new NotSupportedException();
        public Task<List<CustomerDto>> SearchAsync(string? search = null) => pending;
        public Task<List<CustomerDto>> GetAllAsync() => throw new InvalidOperationException("Unbounded list forbidden in POS selector.");
        public Task<CatalogPage<CustomerDto>> GetPageAsync(int page, int size = 100, string? search = null) => throw new NotSupportedException();
        public Task<CustomerDto?> GetByIdAsync(string id) => throw new NotSupportedException();
        public Task AddAsync(CustomerDto customer) => throw new NotSupportedException();
        public Task UpdateAsync(CustomerDto customer) => throw new NotSupportedException();
        public Task UpdateDebtAsync(string customerId, decimal newDebt) => throw new NotSupportedException();
        public Task AddPaymentAsync(CustomerPaymentDto payment) => throw new NotSupportedException();
        public Task<bool> RegisterCustomerPaymentAsync(string customerId, decimal amount, string method, string notes, string? operationId = null) => throw new NotSupportedException();
        public Task<List<CustomerPaymentDto>> GetPaymentsAsync(string customerId) => throw new NotSupportedException();
        public Task DeleteAsync(string id) => throw new NotSupportedException();
        public Task UpdatePointsAsync(string customerId, double points) => throw new NotSupportedException();
        public Task<FiscalClientDto?> GetFiscalDataAsync(string customerId) => throw new NotSupportedException();
        public Task SaveFiscalDataAsync(FiscalClientDto data) => throw new NotSupportedException();
    }
}
