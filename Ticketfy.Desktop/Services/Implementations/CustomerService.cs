using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class CustomerService : ICustomerService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public CustomerService(AppDbContext db, IEscPosPrinterService? printerService = null, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public CustomerService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }
    private Task<OperationIdentity> AuthorizeAsync(AppDbContext db, string permission, bool shift = false) =>
        (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(db, permission, shift);
    private static CustomerDto Map(CustomerEntity c) => new(c.Id, c.Name, c.Phone, c.Email, c.Rfc, c.CreditLimit, c.Debt, c.PuntosSaldo, c.Debt, c.CustomerCode);
    public async Task<List<CustomerDto>> GetAllAsync()
    {
        var page = await GetPageAsync(0, LegacyCatalogReadPolicy.Limit);
        LegacyCatalogReadPolicy.RequireComplete(page.HasMore);
        return page.Items;
    }
    public async Task<CustomerDto?> GetByIdAsync(string id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeAsync(db, PermissionCatalog.CustomersView);
        var c = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        return c is null ? null : Map(c);
    }
    public async Task<List<CustomerDto>> SearchAsync(string? search = null) => (await GetPageAsync(0, 100, search)).Items;
    public async Task<CatalogPage<CustomerDto>> GetPageAsync(int page, int size = 100, string? search = null)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeAsync(db, PermissionCatalog.CustomersView);
        var query = db.Customers.AsNoTracking().Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string value = search.Trim().ToLowerInvariant();
            query = query.Where(c => c.Name.ToLower().Contains(value) || c.CustomerCode.ToLower().Contains(value)
                || c.Phone.Contains(value) || c.Rfc.ToLower().Contains(value) || c.Email.ToLower().Contains(value));
        }
        var rows = await query.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip(offset).Take(size + 1).ToListAsync();
        return new(rows.Take(size).Select(Map).ToList(), rows.Count > size);
    }
    public async Task AddAsync(CustomerDto customer)
    {
        customer = Ticketfy.Core.Helpers.CustomerInput.Normalize(customer);
        if (customer.Debt != 0 || customer.PuntosSaldo != 0) throw new InvalidOperationException("Los saldos iniciales requieren una migración conciliada.");
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.CustomersCreateEdit);
        if (customer.CreditLimit > 0) await AuthorizeAsync(db, PermissionCatalog.CustomersCreditLimit);
        string id = string.IsNullOrWhiteSpace(customer.Id) ? Guid.NewGuid().ToString("N") : customer.Id;
        var existing = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        if (existing is not null)
        {
            if (!existing.IsActive || !SameProfile(existing, customer, allowGeneratedCode: true))
                throw new InvalidOperationException("El cliente de esta alta ya existe con otros datos. Revise el directorio antes de continuar.");
            return;
        }
        await RequireAvailableCodeAsync(db, customer, id);
        var entity = new CustomerEntity
        {
            Id = id,
            Name = customer.Name.Trim(),
            Phone = customer.Phone,
            Email = customer.Email,
            Rfc = customer.Rfc,
            CreditLimit = customer.CreditLimit,
            CustomerCode = string.IsNullOrWhiteSpace(customer.CustomerCode) ? "CLI-" + Guid.NewGuid().ToString("N") : customer.CustomerCode.Trim()
        };
        db.Customers.Add(entity);
        CommercialLedger.Journal(db, actor, "CUSTOMER:" + id, "CUSTOMER_CREATED", id, 0, "Alta de cliente");
        await db.SaveChangesAsync();
    }
    public async Task UpdateAsync(CustomerDto customer)
    {
        customer = Ticketfy.Core.Helpers.CustomerInput.Normalize(customer);
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.CustomersCreateEdit);
        var c = await db.Customers.SingleAsync(c => c.Id == customer.Id);
        if (c.Debt != customer.Debt || c.PuntosSaldo != customer.PuntosSaldo)
            throw new InvalidOperationException("El saldo cambió. Actualice los datos; los saldos no se editan directamente.");
        if (c.CreditLimit != customer.CreditLimit) await AuthorizeAsync(db, PermissionCatalog.CustomersCreditLimit);
        if (SameProfile(c, customer)) return;
        await RequireAvailableCodeAsync(db, customer, c.Id);
        c.Name = customer.Name.Trim(); c.Phone = customer.Phone; c.Email = customer.Email; c.Rfc = customer.Rfc;
        c.CreditLimit = customer.CreditLimit; c.CustomerCode = customer.CustomerCode;
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "CUSTOMER_UPDATED", c.Id, 0, "Datos y límite de crédito");
        await db.SaveChangesAsync();
    }
    private static bool SameProfile(CustomerEntity current, CustomerDto draft, bool allowGeneratedCode = false) =>
        current.Name == draft.Name && current.Phone == draft.Phone && current.Email == draft.Email
        && current.Rfc == draft.Rfc && current.CreditLimit == draft.CreditLimit
        && (current.CustomerCode == draft.CustomerCode || (allowGeneratedCode && draft.CustomerCode.Length == 0));

    private static async Task RequireAvailableCodeAsync(AppDbContext db, CustomerDto draft, string id)
    {
        if (draft.CustomerCode.Length > 0 && await db.Customers.AnyAsync(c => c.Id != id && c.CustomerCode == draft.CustomerCode))
            throw new InvalidOperationException("El código de cliente ya está asignado. Use otro código.");
    }
    public Task UpdateDebtAsync(string customerId, decimal newDebt) => throw new InvalidOperationException("La deuda sólo cambia mediante ventas, abonos y notas de crédito.");
    public Task UpdatePointsAsync(string customerId, double points) => throw new InvalidOperationException("Los puntos sólo cambian mediante movimientos identificados.");
    public Task AddPaymentAsync(CustomerPaymentDto payment) => RegisterCustomerPaymentAsync(payment.CustomerId, payment.Amount, payment.Method, payment.Notes, payment.Id);
    public async Task<bool> RegisterCustomerPaymentAsync(string customerId, decimal amount, string method, string notes, string? operationId = null)
    {
        string operation = CommercialLedger.OperationId(operationId);
        if (amount <= 0 || CommercialLedger.Money(amount) != amount) throw new InvalidOperationException("Importe de abono inválido.");
        if (method is not ("Efectivo" or "Cash" or "Transferencia")) throw new InvalidOperationException("Medio no admitido; use efectivo o transferencia preparada y confirmada.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.CashInOut, true);
        await PostCustomerPaymentAsync(db, actor, customerId, amount, method, notes, operation);
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return true;
    }

    // Caller owns authorization and the transaction. Supervised transfer recovery
    // uses the original financial identity, never an impersonated session.
    internal static async Task PostCustomerPaymentAsync(AppDbContext db, OperationIdentity actor, string customerId,
        decimal amount, string method, string notes, string operation)
    {
        await CommandIdentity.BindAsync(db, actor, operation, "CUSTOMER_PAYMENT", customerId, amount, method, notes);
        var previous = await db.CustomerPayments.SingleOrDefaultAsync(p => p.OperationId == operation);
        if (previous is not null)
        {
            if (previous.CustomerId != customerId || previous.Amount != amount || previous.Method != method)
                throw new InvalidOperationException("Clave de abono reutilizada con datos diferentes.");
            return;
        }
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId);
        TenderConfirmationEntity? proof = method == "Transferencia"
            ? await IncomingTransferService.RequireProofAsync(db, actor, operation, amount, IncomingTransferService.CustomerPurpose, customerId) : null;
        var entries = await db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == AccountKind.Customer && e.AccountId == customerId).ToListAsync();
        if (entries.Sum(e => e.Amount) != customer.Debt) throw new InvalidOperationException("El estado de cuenta requiere conciliación antes de aceptar abonos.");
        long cents = Convert.ToInt64(amount * 100m);
        if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE customers SET debt = debt - {cents} WHERE id = {customerId} AND debt >= {cents}") != 1)
            throw new InvalidOperationException("El abono excede el saldo pendiente.");
        var payment = new CustomerPaymentEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            OperationId = operation,
            CustomerId = customerId,
            Amount = amount,
            Method = method,
            Notes = notes,
            ShiftId = actor.ShiftId,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId,
            Date = DateTimeOffset.UtcNow.ToString("O"),
            Status = PaymentStatus.Completed
        };
        db.CustomerPayments.Add(payment);
        decimal left = amount;
        foreach (var document in entries.GroupBy(e => e.DocumentId).OrderBy(g => g.Min(e => e.CreatedAtUtc)))
        {
            decimal allocation = Math.Min(left, Math.Max(0m, document.Sum(e => e.Amount)));
            if (allocation <= 0) continue;
            CommercialLedger.Account(db, actor, operation, AccountKind.Customer, customerId, document.Key, -allocation, "CUSTOMER_PAYMENT");
            left -= allocation;
            if (left == 0) break;
        }
        if (left != 0) throw new InvalidOperationException("No se pudo aplicar el abono a documentos pendientes.");
        if (proof is null) await CommercialLedger.CashAsync(db, actor, operation, payment.Id, amount, false, MovementType.AbonoCliente, notes);
        else db.IncomingTransferReceipts.Add(IncomingTransferService.Receipt(actor, proof, payment.Id));
        CommercialLedger.Journal(db, actor, operation, "CUSTOMER_PAYMENT_COMPLETED", payment.Id, amount, method);
    }
    public async Task<AccountStatementDto> GetStatementAsync(string customerId)
    {
        var page = await GetStatementPageAsync(customerId, size: CommercialLedgerReader.LegacyLimit);
        CommercialLedgerReader.RequireComplete(page.HasMore);
        return new(customerId, page.Balance, page.Entries, page.HasUnreconciledHistory);
    }
    public async Task<AccountStatementPageDto> GetStatementPageAsync(string customerId, int page = 0, int size = 100, LedgerReadCut? cut = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await AuthorizeAsync(db, PermissionCatalog.CustomersView);
        return await CommercialLedgerReader.AccountAsync(db, AccountKind.Customer, customerId, page, size, cut);
    }
    public async Task<List<CustomerPaymentDto>> GetPaymentsAsync(string customerId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeAsync(db, PermissionCatalog.CustomersView);
        return (await db.CustomerPayments.AsNoTracking().Where(p => p.CustomerId == customerId).OrderByDescending(p => p.Date).ToListAsync())
            .Select(p => new CustomerPaymentDto(p.Id, p.CustomerId, p.Date, p.Amount, p.Method, p.Notes)).ToList();
    }
    public async Task DeleteAsync(string id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.CustomersDelete);
        var c = await db.Customers.SingleOrDefaultAsync(c => c.Id == id);
        if (c is null || !c.IsActive) return;
        c.IsActive = false;
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "CUSTOMER_INACTIVATED", id, 0, "Baja lógica");
        await db.SaveChangesAsync();
    }
    public async Task<FiscalClientDto?> GetFiscalDataAsync(string customerId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await AuthorizeAsync(db, PermissionCatalog.CustomersView);
        var f = await db.FiscalClients.FindAsync(customerId);
        return f is null ? null : new(f.Id, f.Id, f.Rfc, f.RazonSocial, f.RegimenFiscal, f.CodigoPostal, f.UsoCfdi);
    }
    public async Task SaveFiscalDataAsync(FiscalClientDto data)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await AuthorizeAsync(db, PermissionCatalog.CustomersCreateEdit);
        var f = await db.FiscalClients.FindAsync(data.Id);
        if (f is null) { f = new FiscalClientEntity { Id = data.Id }; db.FiscalClients.Add(f); }
        f.Rfc = data.Rfc; f.RazonSocial = data.RazonSocial; f.CodigoPostal = data.CodigoPostal; f.RegimenFiscal = data.RegimenFiscal; f.UsoCfdi = data.UsoCfdi;
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "CUSTOMER_FISCAL_UPDATED", data.Id, 0, "Datos fiscales");
        await db.SaveChangesAsync();
    }
}
