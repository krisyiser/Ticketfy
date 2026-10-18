using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
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
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.PurchasesManage, false);
        var purchases = await db.Purchases.AsNoTracking().OrderByDescending(p => p.Date).ToListAsync();
        var items = await db.PurchaseItems.AsNoTracking().ToListAsync();
        return purchases.Select(p => Map(p, items.Where(i => i.PurchaseId == p.Id))).ToList();
    }
    public async Task<PurchaseDto?> GetByIdAsync(string id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.PurchasesManage, false);
        var p = await db.Purchases.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
        return p is null ? null : Map(p, await db.PurchaseItems.AsNoTracking().Where(i => i.PurchaseId == id).ToListAsync());
    }
    public async Task<PurchaseDto> RegisterPurchaseAsync(PurchaseDto dto)
    {
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
        var existing = await db.Purchases.SingleOrDefaultAsync(p => p.OperationId == operation);
        if (existing is not null)
        {
            if (existing.SupplierId != dto.SupplierId || existing.TotalCost != dto.Items.Sum(i => CommercialLedger.Money(i.UnitPrice * (decimal)i.Quantity)))
                throw new InvalidOperationException("Clave de compra reutilizada con datos distintos.");
            return Map(existing, await db.PurchaseItems.Where(i => i.PurchaseId == existing.Id).ToListAsync());
        }
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == dto.SupplierId && s.IsActive == 1)
            ?? throw new InvalidOperationException("Seleccione un proveedor activo.");
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
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == item.ProductId && p.IsActive && !p.IsKit)
                ?? throw new InvalidOperationException("Producto inexistente, inactivo o kit no comprable.");
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
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return Map(purchase, itemEntities);
    }
    public async Task PayAsync(string purchaseId, decimal amount, string method, string? reference, string operationId)
    {
        CommercialLedger.OperationId(operationId);
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db);
        await CommandIdentity.BindAsync(db, actor, operationId, "SUPPLIER_PAYMENT", purchaseId, amount, method, reference);
        var previous = await db.SupplierPayments.SingleOrDefaultAsync(p => p.OperationId == operationId);
        if (previous is not null)
        {
            if (previous.PurchaseId != purchaseId || previous.Amount != amount) throw new InvalidOperationException("Clave de pago reutilizada.");
            return;
        }
        var purchase = await db.Purchases.SingleAsync(p => p.Id == purchaseId);
        if (purchase.LedgerVersion == 0 && !await db.HistoricalBalanceReconciliations.AnyAsync(r => r.Scope == HistoricalBalanceScope.SupplierPurchase && r.TargetId == purchaseId))
            throw new InvalidOperationException("Concilie primero el saldo de esta compra histórica con evidencia del proveedor.");
        var entries = await db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == AccountKind.Supplier && e.AccountId == purchase.SupplierId && e.DocumentId == purchaseId).ToListAsync();
        if (amount <= 0 || amount > entries.Sum(e => e.Amount)) throw new InvalidOperationException("El pago excede el saldo pendiente.");
        await PostPaymentAsync(db, actor, purchase, amount, method, reference, operationId);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    private static async Task PostPaymentAsync(AppDbContext db, OperationIdentity actor, PurchaseEntity purchase,
        decimal amount, string method, string? reference, string operation)
    {
        TenderKind kind = method switch
        {
            "Efectivo" or "Cash" => TenderKind.Cash,
            "Tarjeta" or "Card" => TenderKind.Card,
            "Transferencia" or "Transferencia SPEI" or "Transfer" => TenderKind.Transfer,
            _ => throw new InvalidOperationException("Medio de pago no admitido.")
        };
        if (kind != TenderKind.Cash && string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException("Indique la referencia del pago al proveedor.");
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
        CommercialLedger.OperationId(operationId);
        if (quantity <= 0 || Math.Round(quantity, 3) != quantity || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Indique cantidad y motivo.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await AuthorizeAsync(db);
        await CommandIdentity.BindAsync(db, actor, operationId, "SUPPLIER_RETURN", purchaseItemId, quantity, reason);
        var prior = await db.SupplierReturns.SingleOrDefaultAsync(r => r.OperationId == operationId);
        if (prior is not null)
        {
            if (prior.PurchaseItemId != purchaseItemId || prior.Quantity != quantity) throw new InvalidOperationException("Clave de devolución reutilizada.");
            return;
        }
        var line = await db.PurchaseItems.AsNoTracking().SingleAsync(p => p.Id == purchaseItemId);
        var purchase = await db.Purchases.AsNoTracking().SingleAsync(p => p.Id == line.PurchaseId);
        if (purchase.LedgerVersion == 0) throw new InvalidOperationException("La compra histórica requiere conciliación asistida.");
        var returned = await db.SupplierReturns.AsNoTracking().Where(r => r.PurchaseItemId == purchaseItemId).ToListAsync();
        decimal cumulative = returned.Sum(r => r.Quantity) + quantity;
        if (cumulative > (decimal)line.Quantity) throw new InvalidOperationException("La cantidad excede lo recibido.");
        decimal amount = CommercialLedger.Money(line.TotalPrice * cumulative / (decimal)line.Quantity) - returned.Sum(r => r.Amount);
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
        await using var db = await _factory.CreateDbContextAsync();
        await (_operations ?? throw new UnauthorizedAccessException()).RequireAsync(db, PermissionCatalog.SuppliersManage, false);
        var entries = await db.AccountEntries.AsNoTracking().Where(e => e.AccountKind == AccountKind.Supplier && e.AccountId == supplierId)
            .OrderBy(e => e.CreatedAtUtc).ToListAsync();
        bool historical = await db.Purchases.AnyAsync(p => p.SupplierId == supplierId && p.LedgerVersion == 0
            && !db.HistoricalBalanceReconciliations.Any(r => r.Scope == HistoricalBalanceScope.SupplierPurchase && r.TargetId == p.Id));
        return new(supplierId, entries.Sum(e => e.Amount), entries, historical);
    }
    private static PurchaseDto Map(PurchaseEntity p, IEnumerable<PurchaseItemEntity> items) => new(p.Id, p.SupplierId,
        p.SupplierName, p.InvoiceNumber, p.Date, p.TotalCost, p.Notes,
        items.Select(i => new PurchaseItemDto(i.Id, i.PurchaseId, i.ProductId, i.ProductName, i.UnitPrice, i.Quantity, i.TotalPrice)).ToList(),
        p.OperationId, DueDate: p.DueDate);
}
