using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Services;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;

namespace Ticketfy.Desktop.Tests;

internal static class CommercialTestSession
{
    internal static readonly Guid UserId = Guid.Parse("a77ce654-3fd3-4510-b577-f6fa8fb9ad1f");
    public static CommercialOperationContext Create(AppDbContext db, bool openShift = true, string register = "TEST-REGISTER", Guid? userId = null, string role = "ADMINISTRADOR")
    {
        Guid id = userId ?? UserId;
        var user = db.Users.SingleOrDefault(u => u.Id == id);
        if (user is null)
        {
            user = new UserEntity { Id = id, FullName = "Operador de prueba", Username = id.ToString(), Role = UserRole.Admin, RoleString = role, IsActive = true };
            db.Users.Add(user);
        }
        if (openShift && !db.Shifts.Any(s => s.RegisterId == register && s.IsOpen == 1))
            db.Shifts.Add(new ShiftEntity
            {
                Id = register == "TEST-REGISTER" ? "SHIFT-1" : Guid.NewGuid().ToString("N"),
                RegisterId = register,
                CashierUserId = id.ToString(),
                IsOpen = 1,
                StartTime = DateTimeOffset.UtcNow.ToString("O")
            });
        db.SaveChanges();
        var session = new SessionManager();
        var permissions = new UserRolePermissionEngine();
        session.StartSession(permissions.MapToModel(user));
        return new CommercialOperationContext(session, permissions, register);
    }
    public static CommercialOperationContext Create(DbContextOptions<AppDbContext> options, bool openShift = true)
    {
        using var db = new AppDbContext(options);
        return Create(db, openShift);
    }

    public static void OpeningDebt(AppDbContext db, string id, decimal amount)
    {
        var customer = db.Customers.Single(c => c.Id == id);
        customer.Debt = amount;
        db.AccountEntries.Add(new AccountEntryEntity
        {
            AccountId = id,
            AccountKind = AccountKind.Customer,
            Amount = amount,
            DocumentId = "OPENING:" + id,
            OperationId = "TEST-OPENING:" + id,
            EntryType = "MIGRATION_OPENING",
            CashierUserId = "system:test-migration",
            RegisterId = "system:test-migration"
        });
        db.SaveChanges();
    }
    public static void ConfirmCard(AppDbContext db, string operationId, decimal amount, string? reference = null)
    {
        db.PaymentTerminalOperations.Add(new PaymentTerminalOperationEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            ExternalReference = operationId,
            IdempotencyKey = operationId,
            Amount = amount,
            ProviderPaymentId = reference ?? "TEST-BANK:" + operationId,
            Status = IntegrationOperationStatus.Completed,
            FundsObserved = true,
            ProviderUpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            // This fixture represents an already validated provider response, never a production token.
            ProviderEvidenceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("SYNTHETIC-BANK-EVIDENCE:" + operationId)))
        });
        db.SaveChanges();
    }

    public static async Task<CommercialOperationContext> PrepareCardCheckoutAsync(IDbContextFactory<AppDbContext> factory, string reference, decimal amount)
    {
        await using var db = await factory.CreateDbContextAsync();
        var actor = Create(db);
        string productId = "BANK-PRODUCT:" + reference;
        db.Products.Add(new ProductEntity { Id = productId, Barcode = productId, Name = "Producto de prueba bancaria", Price = amount, Stock = 2 });
        await db.SaveChangesAsync();
        var sale = new Ticketfy.Data.Dtos.SaleDto(reference, DateTimeOffset.UtcNow.ToString("O"),
            [new(productId, "Producto de prueba bancaria", amount, 0m, 1, "Pza", "General", 0m, amount)],
            amount, 0m, amount, amount, 0m, "Tarjeta", null, false, false, null, CardAmount: amount, OperationId: reference);
        await new Ticketfy.Services.Implementations.SaleService(factory, new(), new SpyAuditService(), actor).BeginCheckoutAsync(sale);
        return actor;
    }
}

internal sealed class FixedTestClock(DateTime utc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
