using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Auth;
using Ticketfy.Services.Implementations;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed class CustomerAndAuthServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly CustomerService _customerService;
    private readonly UserService _userService;
    private readonly AuthService _authService;

    public CustomerAndAuthServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _customerService = new CustomerService(_context, operations: CommercialTestSession.Create(_context));
        _userService = new UserService(_context, operations: CommercialTestSession.Create(_context));
        _authService = new AuthService(_userService);
    }

    [Fact]
    public async Task AddPaymentAsync_ShouldReduceDebtAtomically()
    {
        // Customer initial debt = 500
        var customer = new CustomerDto("CUST-PAY-1", "Carlos M.", "5553334444", "carlos@carlos.com", "RFC123", 1000.0, 0.0);
        await _customerService.AddAsync(customer);
        CommercialTestSession.OpeningDebt(_context, customer.Id, 500m);

        // Make payment of 200
        var payment = new CustomerPaymentDto("PAY-1", "CUST-PAY-1", DateTimeOffset.UtcNow.ToString("o"), 200.0);
        await _customerService.AddPaymentAsync(payment);

        // Verify remaining debt = 300
        var updated = await _customerService.GetByIdAsync("CUST-PAY-1");
        Assert.NotNull(updated);
        Assert.Equal(300m, updated.Debt);

        var payments = await _customerService.GetPaymentsAsync("CUST-PAY-1");
        Assert.Single(payments);
        Assert.Equal(200m, payments[0].Amount);
    }

    [Fact]
    public async Task AuthService_Login_ShouldAuthenticateValidUserWithPbkdf2()
    {
        var guidId = "00000000-0000-0000-0000-000000000001";
        await _userService.SaveAsync(guidId, "AdminUser", "ADMIN", "Admin", "Synthetic securePass123", null);

        // Valid Login
        var success = await _authService.LoginAsync("ADMIN", "Synthetic securePass123");
        Assert.True(success);
        Assert.True(_authService.IsAuthenticated);
        Assert.Equal("ADMINISTRADOR", _authService.CurrentUser?.Rol);

        // Invalid Login
        var invalid = await _authService.LoginAsync("ADMIN", "wrongPass");
        Assert.False(invalid);
    }

    [Fact]
    public async Task AddPaymentAsync_ShouldThrowForZeroOrNegativeAmount()
    {
        var payment = new CustomerPaymentDto("PAY-INV", "CUST-PAY-1", DateTimeOffset.UtcNow.ToString("o"), 0.0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _customerService.AddPaymentAsync(payment));
    }

    [Fact]
    public async Task AuthService_Login_ShouldFailForInactiveUser()
    {
        var guidId = "00000000-0000-0000-0000-000000000002";
        await _userService.SaveAsync(guidId, "DisabledUser", "CAJERO_DIS", "Cajero", "Synthetic pass123", null);

        // Deactivate user in DB
        var userEntity = await _context.Users.FindAsync(Guid.Parse(guidId));
        if (userEntity != null)
        {
            userEntity.IsActive = false;
            await _context.SaveChangesAsync();
        }

        var result = await _authService.LoginAsync("CAJERO_DIS", "Synthetic pass123");
        Assert.False(result);
    }

    [Fact]
    public async Task AuthService_ManagerApprovalRequiresPasswordEvenForCorrectPin()
    {
        var guidId = "00000000-0000-0000-0000-000000000003";
        // A manager's valid quick-access PIN is not a primary credential for an approval.
        await _userService.SaveAsync(guidId, "Manager", "GERENTE_01", "Gerente", "manager-test-password", "9999");

        var valid = await _authService.VerifyManagerPinAsync(guidId, "9999");
        Assert.False(valid);
        Assert.True(await _authService.VerifyManagerPasswordAsync(guidId, "manager-test-password"));

        var invalid = await _authService.VerifyManagerPinAsync(guidId, "0000");
        Assert.False(invalid);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
