using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public partial class ItemKitService : IItemKitService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public ItemKitService(AppDbContext db, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public ItemKitService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }

    public async Task SaveAsync(KitDraft draft, string operationId)
    {
        if (draft is null) throw new KitCommandRejectedException("Se requiere el borrador completo del combo.");
        // Own the list across asynchronous boundaries; a UI edit must not change an in-flight command.
        draft = draft with { Items = draft.Items?.ToArray()! };
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            operationId = CommercialLedger.OperationId(operationId);
            var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
                .RequireAsync(db, draft.Revision.HasValue ? PermissionCatalog.InventoryEdit : PermissionCatalog.InventoryCreate);
            ValidateDraft(draft);
            bool replay = await db.CommandReceipts.AnyAsync(c => c.OperationId == operationId);
            await CommandIdentity.BindAsync(db, actor, operationId, "KIT_SAVE", draft.ProductId, draft.Barcode,
                draft.Name, draft.Price, draft.Description, draft.Revision, draft.IsActive,
                string.Concat(draft.Items.Select(i => CommandIdentity.Hash(i.ProductId, i.Quantity))));
            if (replay) return;

            var product = await db.Products.SingleOrDefaultAsync(p => p.Id == draft.ProductId);
            if (product is null ? draft.Revision.HasValue : !product.IsKit || draft.Revision != product.StockVersion)
                throw new KitConflictException();
            if (product is not null && product.IsActive != draft.IsActive || product is null && !draft.IsActive)
                throw new KitCommandRejectedException("Cambie la habilitación desde el directorio, no desde el formulario de composición.");
            string barcode = draft.Barcode.Trim();
            if (await db.Products.AnyAsync(p => p.Id != draft.ProductId && p.Barcode == barcode))
                throw new KitCommandRejectedException("El código ya pertenece a otro producto o combo.");

            var matches = await db.ItemKits.Where(k => k.ParentProductId == draft.ProductId).Take(2).ToListAsync();
            if (matches.Count > 1 || product is not null && matches.Count != 1 || product is null && matches.Count != 0)
                throw new KitCommandRejectedException("El vínculo de la receta requiere revisión; no se reemplazó ninguna composición.");
            var recipe = matches.SingleOrDefault();
            var previousItems = recipe is null ? [] : await db.ItemKitItems.Where(i => i.ItemKitId == recipe.Id)
                .OrderBy(i => i.Id).Take(KitInventoryGraph.MaximumDirectComponents + 1).ToListAsync();
            if (previousItems.Count > KitInventoryGraph.MaximumDirectComponents)
                throw new KitCommandRejectedException("La receta supera 200 componentes. Requiere revisión sin truncar el historial.");
            var before = product is null ? null : Snapshot(product, recipe!, previousItems);
            if (product is null)
            {
                product = new ProductEntity
                {
                    Id = draft.ProductId,
                    IsKit = true,
                    Category = "Promociones",
                    Unit = "Pza",
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O")
                };
                db.Products.Add(product);
                recipe = new ItemKitEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ParentProductId = product.Id,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O")
                };
                db.ItemKits.Add(recipe);
            }
            else AdvanceRevision(product);
            product.Name = draft.Name.Trim(); product.Barcode = barcode; product.Price = draft.Price;
            recipe!.Name = product.Name; recipe.KitBarcode = barcode; recipe.Price = product.Price;
            recipe.Description = draft.Description;
            db.ItemKitItems.RemoveRange(previousItems);
            var items = draft.Items.Select(i => new ItemKitItemEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                ItemKitId = recipe.Id,
                ProductId = i.ProductId,
                Quantity = i.Quantity
            }).ToList();
            db.ItemKitItems.AddRange(items);
            // Validate provisional rows using exactly the same bounded graph as checkout, in this transaction.
            await db.SaveChangesAsync();
            await ValidateCompositionAsync(db, product);
            CommercialLedger.Journal(db, actor, operationId, "KIT_SAVED", product.Id, 0,
                JsonSerializer.Serialize(new { Before = before, After = Snapshot(product, recipe, items) }));
            await db.SaveChangesAsync();
        }
        catch (KitCommandRejectedException) { throw; }
        catch (DbUpdateConcurrencyException) { throw new KitConflictException(); }
        catch (InvalidOperationException ex) { throw new KitCommandRejectedException(ex.Message); }
        // Do not classify a lost commit response as a rejection; replay must keep the same identity.
        await tx.CommitAsync();
    }

    private static void ValidateDraft(KitDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.ProductId) || draft.ProductId.Length > 128
            || string.IsNullOrWhiteSpace(draft.Barcode) || draft.Barcode.Length > 128
            || string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Length > 200
            || draft.Description is null || draft.Description.Length > 2000
            || draft.Price < 0 || draft.Price > long.MaxValue / 100m || CommercialLedger.Money(draft.Price) != draft.Price
            || draft.Items is null || draft.Items.Count is < 1 or > KitInventoryGraph.MaximumDirectComponents
            || draft.Items.Any(i => i is null || string.IsNullOrWhiteSpace(i.ProductId) || i.ProductId.Length > 128
                || i.ProductId == draft.ProductId || !double.IsFinite(i.Quantity) || i.Quantity <= 0
                || i.Quantity > (double)(long.MaxValue / 1000m) || Math.Round(i.Quantity, 3) != i.Quantity))
            throw new KitCommandRejectedException("Revise nombre (200), código (128), notas (2000), precio y de 1 a 200 componentes con cantidades válidas.");
    }

    private static KitDraft Snapshot(ProductEntity product, ItemKitEntity recipe, List<ItemKitItemEntity> items) =>
        new(product.Id, product.Barcode ?? "", product.Name, product.Price, recipe.Description,
            items.Select(i => new ItemKitItemDto(i.Id, i.ItemKitId, i.ProductId, "", i.Quantity)).ToList(),
            product.StockVersion, product.IsActive);

    private static void AdvanceRevision(ProductEntity product)
    {
        if (product.StockVersion < 0 || product.StockVersion == long.MaxValue)
            throw new KitCommandRejectedException("La revisión del combo requiere revisión técnica.");
        product.StockVersion++;
    }

    private static async Task ValidateCompositionAsync(AppDbContext db, ProductEntity product)
    {
        // Inactive recipes may be repaired without being silently reactivated.
        var validationRoot = new ProductEntity
        {
            Id = product.Id,
            Name = product.Name,
            IsKit = true,
            IsActive = true,
            IsBulk = product.IsBulk,
            Unit = product.Unit
        };
        (await KitInventoryGraph.LoadAsync(db, [validationRoot])).RequirePlan(product.Id);
    }

    public async Task SetActiveAsync(string productId, bool active, string operationId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            operationId = CommercialLedger.OperationId(operationId);
            var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión requerida."))
                .RequireAsync(db, active ? PermissionCatalog.InventoryEdit : PermissionCatalog.InventoryDelete);
            bool replay = await db.CommandReceipts.AnyAsync(c => c.OperationId == operationId);
            await CommandIdentity.BindAsync(db, actor, operationId, "KIT_STATUS", productId, active);
            if (replay) return;
            var product = await db.Products.SingleOrDefaultAsync(p => p.Id == productId && p.IsKit)
                ?? throw new KitCommandRejectedException("El combo ya no existe. Actualice el directorio.");
            if (active) await ValidateCompositionAsync(db, product);
            if (product.IsActive != active)
            {
                AdvanceRevision(product); product.IsActive = active;
                CommercialLedger.Journal(db, actor, operationId, active ? "KIT_ACTIVATED" : "KIT_INACTIVATED",
                    productId, 0, $"Habilitación del combo; revisión {product.StockVersion}. Se conserva la receta.");
            }
            // Even a no-op needs a durable receipt, so its replay cannot undo a later change.
            await db.SaveChangesAsync();
        }
        catch (KitCommandRejectedException) { throw; }
        catch (DbUpdateConcurrencyException) { throw new KitConflictException(); }
        catch (InvalidOperationException ex) { throw new KitCommandRejectedException(ex.Message); }
        await tx.CommitAsync();
    }

    public Task<bool> DeductKitStockAsync(string kitId, double kitQuantity) =>
        throw new InvalidOperationException("El inventario de combos se descuenta únicamente dentro de una venta.");
}
