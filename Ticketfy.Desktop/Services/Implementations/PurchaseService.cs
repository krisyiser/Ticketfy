using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Operations;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public class PurchaseService : IPurchaseService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public PurchaseService(AppDbContext db, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public PurchaseService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }
    private Task<OperationIdentity> AuthorizeAsync(AppDbContext db) =>
        (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(db, PermissionCatalog.PurchasesManage);
    public async Task<List<PurchaseDto>> GetAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.PurchasesManage, false);
        var purchases = await PurchaseDocumentReader.PageHeadersAsync(db, 0, LegacyCatalogReadPolicy.Limit + 1);
        LegacyCatalogReadPolicy.RequireComplete(purchases.Count > LegacyCatalogReadPolicy.Limit);
        return await ReadAuthorizedAsync(db, purchases, actor);
    }
    public async Task<List<PurchaseDto>> GetPageAsync(int page, int pageSize = 100, string? search = null)
    {
        if (page < 0 || page > 1000000) throw new ArgumentOutOfRangeException(nameof(page));
        if (search?.Length > 128 || search?.Any(char.IsControl) == true)
            throw new ArgumentException("La búsqueda admite hasta 128 caracteres sin controles.", nameof(search));
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.PurchasesManage, false);
        var purchases = await PurchaseDocumentReader.PageHeadersAsync(db, checked(page * pageSize), pageSize, search);
        return await ReadAuthorizedAsync(db, purchases, actor);
    }
    public async Task<PurchaseDto?> GetByIdAsync(string id)
    {
        PurchaseDocumentReader.RequireKey(id, nameof(id));
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.PurchasesManage, false);
        var p = await PurchaseDocumentReader.HeaderByIdAsync(db, id);
        return (await ReadAuthorizedAsync(db, p is null ? [] : [p], actor)).SingleOrDefault();
    }
    private async Task<List<PurchaseDto>> ReadAuthorizedAsync(AppDbContext db, List<PurchaseEntity> purchases, OperationIdentity actor)
    {
        var result = await PurchaseDocumentReader.ReadAsync(db, purchases);
        if (await _operations!.RequireAsync(db, PermissionCatalog.PurchasesManage, false) != actor)
            throw new UnauthorizedAccessException("La sesión cambió durante la consulta. Vuelva a consultar.");
        return result;
    }
    public async Task<PurchaseFinancialPage> GetFinancialHistoryAsync(string id, int page = 0, int size = 100)
    {
        PurchaseDocumentReader.RequireKey(id, nameof(id));
        if (page < 0 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        _ = checked(page * size);
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        var actor = await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.PurchasesManage, false);
        var header = await PurchaseDocumentReader.HeaderByIdAsync(db, id) ?? throw new InvalidOperationException("Compra no encontrada.");
        var document = (await PurchaseDocumentReader.ReadAsync(db, [header])).Single();
        var result = await PurchaseFinancialReader.PageAsync(db, document, page, size);
        if (await _operations.RequireAsync(db, PermissionCatalog.PurchasesManage, false) != actor) throw new UnauthorizedAccessException("La sesión cambió. Vuelva a consultar.");
        return result;
    }
    public async Task<PurchaseDto> RegisterPurchaseAsync(PurchaseDto dto)
    {
        CommercialDocumentLimits.ValidatePurchase(dto);
        string operation = CommercialLedger.OperationId(dto.OperationId ?? dto.Id);
        if (!string.IsNullOrWhiteSpace(dto.DueDate) && !DateTime.TryParseExact(dto.DueDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _)) throw new InvalidOperationException("El vencimiento debe tener formato aaaa-mm-dd.");
        if (dto.Items.Count == 0 || dto.Items.Any(i => !double.IsFinite(i.Quantity) || i.Quantity <= 0 ||
            Math.Round(i.Quantity, 3) != i.Quantity || i.UnitPrice <= 0 || CommercialLedger.Money(i.UnitPrice) != i.UnitPrice))
            throw new InvalidOperationException("La compra requiere partidas con cantidades y costos positivos.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db);
        await CommandIdentity.BindAsync(db, actor, operation, "PURCHASE", dto.SupplierId, dto.InvoiceNumber, dto.PaidAmount,
            dto.PaymentMethod, dto.PaymentReference, dto.DueDate, string.Join("", dto.Items.Select(i => CommandIdentity.Hash(i.ProductId, i.Quantity, i.UnitPrice))));
        var existing = await PurchaseDocumentReader.HeaderByOperationAsync(db, operation);
        if (existing is not null)
        {
            if (existing.SupplierId != dto.SupplierId || existing.TotalCost != dto.Items.Sum(i => CommercialLedger.Money(i.UnitPrice * (decimal)i.Quantity)))
                throw new InvalidOperationException("Clave de compra reutilizada con datos distintos.");
            return (await PurchaseDocumentReader.ReadAsync(db, [existing])).Single();
        }
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == dto.SupplierId && s.IsActive == 1)
            ?? throw new InvalidOperationException("Seleccione un proveedor activo.");
        var productIds = dto.Items.Select(i => i.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id) && p.IsActive && !p.IsKit)
            .ToDictionaryAsync(p => p.Id);
        if (products.Count != productIds.Length) throw new InvalidOperationException("Producto inexistente, inactivo o kit no comprable.");
        CommercialDocumentLimits.ValidatePurchase(dto with
        {
            SupplierName = supplier.Name,
            Items = dto.Items.Select(i => i with { ProductName = products[i.ProductId].Name }).ToList()
        });
        if (!string.IsNullOrWhiteSpace(dto.InvoiceNumber) && await db.Purchases.AnyAsync(p => p.SupplierId == supplier.Id && p.InvoiceNumber == dto.InvoiceNumber))
            throw new InvalidOperationException("Ya existe esta factura del proveedor.");
        var purchase = new PurchaseEntity
        {
            Id = string.IsNullOrEmpty(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id,
            OperationId = operation,
            SupplierId = supplier.Id,
            SupplierName = supplier.Name,
            InvoiceNumber = dto.InvoiceNumber.Trim(),
            Date = DateTimeOffset.UtcNow.ToString("O"),
            Notes = dto.Notes,
            ShiftId = actor.ShiftId,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId,
            DueDate = dto.DueDate,
            LedgerVersion = 1
        };
        db.Purchases.Add(purchase);
        var itemEntities = new List<PurchaseItemEntity>();
        foreach (var item in dto.Items)
        {
            var product = products[item.ProductId];
            if (!product.IsBulk && item.Quantity != Math.Truncate(item.Quantity)) throw new InvalidOperationException("La unidad no admite fracciones.");
            var line = new PurchaseItemEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                PurchaseId = purchase.Id,
                ProductId = product.Id,
                ProductName = product.Name,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                TotalPrice = CommercialLedger.Money(item.UnitPrice * (decimal)item.Quantity)
            };
            itemEntities.Add(line); db.PurchaseItems.Add(line); purchase.TotalCost += line.TotalPrice;
            await CommercialLedger.StockAsync(db, actor, operation, purchase.Id, product.Id, (decimal)item.Quantity, "PURCHASE", cost: item.UnitPrice);
            long cents = Convert.ToInt64(item.UnitPrice * 100m);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET cost = {cents}, default_supplier_id = {supplier.Id} WHERE id = {product.Id}");
        }
        if (dto.PaidAmount < 0 || dto.PaidAmount > purchase.TotalCost || CommercialLedger.Money(dto.PaidAmount) != dto.PaidAmount)
            throw new InvalidOperationException("Importe pagado inválido.");
        CommercialLedger.Account(db, actor, operation, AccountKind.Supplier, supplier.Id, purchase.Id, purchase.TotalCost, "PURCHASE", dto.DueDate);
        if (dto.PaidAmount > 0)
            await PostPaymentAsync(db, actor, purchase, dto.PaidAmount, dto.PaymentMethod, dto.PaymentReference, operation + ":PAY");
        CommercialLedger.Journal(db, actor, operation, "PURCHASE_RECEIVED", purchase.Id, purchase.TotalCost, purchase.InvoiceNumber);
        await db.SaveChangesAsync();
        var result = (await PurchaseDocumentReader.ReadAsync(db, [purchase])).Single();
        if (!result.DetailsAvailable || result.Financial is null)
            throw new InvalidOperationException("No se pudo verificar la compra nueva. Corrija los datos del documento y catálogo antes de recibir mercancía.");
        await tx.CommitAsync();
        return result;
    }
    public async Task PayAsync(string purchaseId, decimal amount, string method, string? reference, string operationId)
    {
        PurchaseDocumentReader.RequireKey(purchaseId, nameof(purchaseId));
        PurchaseDocumentReader.RequireKey(operationId, nameof(operationId));
        if (reference?.Contains('\0') == true) throw new ArgumentException("La referencia bancaria no admite caracteres nulos.", nameof(reference));
        CommercialLedger.OperationId(operationId);
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db);
        await CommandIdentity.BindAsync(db, actor, operationId, "SUPPLIER_PAYMENT", purchaseId, amount, method, reference);
        var previous = await db.SupplierPayments.AsNoTracking().Where(p => p.OperationId == operationId)
            .Select(p => new { p.PurchaseId, p.Amount }).SingleOrDefaultAsync();
        if (previous is not null)
        {
            if (previous.PurchaseId != purchaseId || previous.Amount != amount) throw new InvalidOperationException("Clave de pago reutilizada.");
            return;
        }
        var purchase = await PurchaseDocumentReader.HeaderByIdAsync(db, purchaseId)
            ?? throw new InvalidOperationException("Compra no encontrada.");
        if (purchase.LedgerVersion == 0 && !await db.HistoricalBalanceReconciliations.AnyAsync(r => r.Scope == HistoricalBalanceScope.SupplierPurchase && r.TargetId == purchaseId))
            throw new InvalidOperationException("Concilie primero el saldo de esta compra histórica con evidencia del proveedor.");
        var original = (await PurchaseDocumentReader.ReadAsync(db, [purchase])).Single();
        if (original.Financial is null) throw new InvalidOperationException(PurchaseFinancialReader.Unavailable);
        decimal balance = 0m;
        await foreach (var entry in db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == AccountKind.Supplier &&
            e.AccountId == purchase.SupplierId && e.DocumentId == purchaseId).Select(e => e.Amount).AsAsyncEnumerable()) balance += entry;
        if (amount <= 0 || amount > balance) throw new InvalidOperationException("El pago excede el saldo pendiente.");
        await PostPaymentAsync(db, actor, purchase, amount, method, reference, operationId);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    private static async Task PostPaymentAsync(AppDbContext db, OperationIdentity actor, PurchaseEntity purchase,
        decimal amount, string method, string? reference, string operation)
    {
        TenderKind kind = TenderMethodCatalog.Parse(method);
        if (reference?.Length > 200 || reference?.Any(char.IsControl) == true)
            throw new InvalidOperationException("La referencia de pago admite hasta 200 caracteres sin controles.");
        if (kind != TenderKind.Cash && string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException("Indique la referencia del pago al proveedor.");
        if (kind != TenderKind.Cash)
        {
            if (reference!.Length > 200) throw new InvalidOperationException("La referencia bancaria admite hasta 200 caracteres.");
            reference = reference.Trim();
            await BankReferenceGuard.RequireUnusedAsync(db, reference);
        }
        if (CommercialLedger.Money(amount) != amount) throw new InvalidOperationException("Importe con precisión inválida.");
        db.SupplierPayments.Add(new SupplierPaymentEntity
        {
            PurchaseId = purchase.Id,
            SupplierId = purchase.SupplierId,
            Amount = amount,
            Kind = kind,
            Reference = reference,
            OperationId = operation,
            ShiftId = actor.ShiftId!,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId
        });
        CommercialLedger.Account(db, actor, operation, AccountKind.Supplier, purchase.SupplierId, purchase.Id, -amount, "SUPPLIER_PAYMENT");
        if (kind == TenderKind.Cash) await CommercialLedger.CashAsync(db, actor, operation, purchase.Id, amount, true, MovementType.CompraEfectivo, "Pago a proveedor");
        CommercialLedger.Journal(db, actor, operation, "SUPPLIER_PAYMENT", purchase.Id, amount, reference ?? method);
    }
    public async Task ReturnAsync(string purchaseItemId, decimal quantity, string reason, string operationId)
    {
        PurchaseDocumentReader.RequireKey(purchaseItemId, nameof(purchaseItemId));
        PurchaseDocumentReader.RequireKey(operationId, nameof(operationId));
        CommercialLedger.OperationId(operationId);
        if (quantity <= 0 || Math.Round(quantity, 3) != quantity || string.IsNullOrWhiteSpace(reason) ||
            reason.Length > 500 || reason.Contains('\0')) throw new InvalidOperationException("Indique cantidad y motivo válido de hasta 500 caracteres, sin nulos.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db);
        await CommandIdentity.BindAsync(db, actor, operationId, "SUPPLIER_RETURN", purchaseItemId, quantity, reason);
        var prior = await db.SupplierReturns.AsNoTracking().Where(r => r.OperationId == operationId)
            .Select(r => new { r.PurchaseItemId, r.Quantity }).SingleOrDefaultAsync();
        if (prior is not null)
        {
            if (prior.PurchaseItemId != purchaseItemId || prior.Quantity != quantity) throw new InvalidOperationException("Clave de devolución reutilizada.");
            return;
        }
        var purchase = await PurchaseDocumentReader.HeaderForLineAsync(db, purchaseItemId)
            ?? throw new InvalidOperationException("Partida de compra no encontrada.");
        if (purchase.LedgerVersion == 0) throw new InvalidOperationException("La compra histórica requiere conciliación asistida.");
        var original = (await PurchaseDocumentReader.ReadAsync(db, [purchase])).Single();
        if (!original.DetailsAvailable) throw new InvalidOperationException(PurchaseDocumentReader.Unavailable);
        if (original.Financial is null) throw new InvalidOperationException(PurchaseFinancialReader.Unavailable);
        var line = original.Items.Single(i => i.Id == purchaseItemId);
        decimal priorQuantity = 0m, priorAmount = 0m;
        await foreach (var returned in db.SupplierReturns.AsNoTracking().Where(r => r.PurchaseItemId == purchaseItemId)
            .Select(r => new { r.Quantity, r.Amount }).AsAsyncEnumerable())
        {
            if (returned.Quantity <= 0 || Math.Round(returned.Quantity, 3) != returned.Quantity ||
                returned.Amount < 0 || CommercialLedger.Money(returned.Amount) != returned.Amount)
                throw new InvalidOperationException("Las devoluciones anteriores requieren conciliación.");
            priorQuantity += returned.Quantity; priorAmount += returned.Amount;
        }
        if (priorQuantity > (decimal)line.Quantity ||
            priorAmount != CommercialLedger.Money(line.TotalPrice * priorQuantity / (decimal)line.Quantity))
            throw new InvalidOperationException("Las devoluciones anteriores no coinciden con la partida original.");
        decimal cumulative = priorQuantity + quantity;
        if (cumulative > (decimal)line.Quantity) throw new InvalidOperationException("La cantidad excede lo recibido.");
        decimal amount = CommercialLedger.Money(line.TotalPrice * cumulative / (decimal)line.Quantity) - priorAmount;
        var entity = new SupplierReturnEntity
        {
            OperationId = operationId,
            PurchaseId = purchase.Id,
            PurchaseItemId = line.Id,
            SupplierId = purchase.SupplierId,
            Quantity = quantity,
            Amount = amount,
            Reason = reason,
            ShiftId = actor.ShiftId!,
            RegisterId = actor.RegisterId,
            CashierUserId = actor.UserId
        };
        db.SupplierReturns.Add(entity);
        await CommercialLedger.StockAsync(db, actor, operationId, entity.Id, line.ProductId, -quantity, "SUPPLIER_RETURN", cost: line.UnitPrice);
        CommercialLedger.Account(db, actor, operationId, AccountKind.Supplier, purchase.SupplierId, purchase.Id, -amount, "SUPPLIER_RETURN");
        CommercialLedger.Journal(db, actor, operationId, "SUPPLIER_RETURN", entity.Id, amount, reason);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<AccountStatementDto> GetStatementAsync(string supplierId)
    {
        var page = await GetStatementPageAsync(supplierId, size: CommercialLedgerReader.LegacyLimit);
        CommercialLedgerReader.RequireComplete(page.HasMore);
        return new(supplierId, page.Balance, page.Entries, page.HasUnreconciledHistory);
    }
    public async Task<AccountStatementPageDto> GetStatementPageAsync(string supplierId, int page = 0, int size = 100, LedgerReadCut? cut = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var snapshot = await ReadSnapshot.BeginAsync(db);
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.SuppliersManage, false);
        return await CommercialLedgerReader.AccountAsync(db, AccountKind.Supplier, supplierId, page, size, cut);
    }
}
