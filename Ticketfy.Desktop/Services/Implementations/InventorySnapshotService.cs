using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Serilog;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Operations;

namespace Ticketfy.Services.Implementations;

/// <summary>Documentary inventory valuation, not a recovery backup. Uses only its injected installation.</summary>
public sealed class InventorySnapshotService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null) : IInventorySnapshotService
{
    private async Task<OperationIdentity> RequireAsync(AppDbContext db)
    {
        var context = operations ?? throw new UnauthorizedAccessException("Sesión requerida.");
        await context.RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.InventoryView, false);
        return await context.RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.ReportsProfits, false);
    }
    public async Task<InventorySnapshotEntity> CreateSnapshotAsync(string notes, string operationId)
    {
        CommercialLedger.OperationId(operationId);
        if (notes.Length > 1000) throw new InvalidOperationException("La nota admite hasta 1000 caracteres.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await RequireAsync(db);
        await operations!.RequireAsync(db, Ticketfy.Core.Security.PermissionCatalog.InventoryStockAdjust, false);
        await CommandIdentity.BindAsync(db, actor, operationId, "INVENTORY_SNAPSHOT", notes);
        var previous = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == operationId);
        if (previous is not null) return previous;
        // Capture all physical products, including inactive stock; virtual kits never duplicate their components.
        var products = db.Products.AsNoTracking().Where(p => !p.IsKit);
        int count = await products.CountAsync();
        if (count > InventoryWorkloadLimits.SnapshotRows)
            throw new InvalidOperationException($"La captura local admite hasta {InventoryWorkloadLimits.SnapshotRows:N0} productos físicos. No se guardó una captura parcial; solicite un procedimiento de mayor volumen.");
        decimal totalValue = 0m;
        await foreach (var product in products.Select(p => new { p.Cost, p.Stock }).AsAsyncEnumerable())
            totalValue += product.Cost * (decimal)product.Stock;
        var snapshot = new InventorySnapshotEntity
        {
            Id = operationId,
            CreatedAt = Ticketfy.Core.Helpers.BusinessTime.Now,
            Notes = notes,
            TotalItems = count,
            TotalValue = CommercialLedger.Money(totalValue)
        };
        db.InventorySnapshots.Add(snapshot);
        CommercialLedger.Journal(db, actor, operationId, "INVENTORY_SNAPSHOT_CREATED", snapshot.Id, snapshot.TotalValue,
            "Captura documental de existencias físicas; no es un respaldo de recuperación.");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // The header, every batch, receipt and journal share one transaction. Tracking
        // stays bounded; an interrupted batch rolls back the whole documentary capture.
        string? after = null;
        int captured = 0;
        while (captured < count)
        {
            var batch = await products.Where(p => after == null || string.Compare(p.Id, after) > 0)
                .OrderBy(p => p.Id).Take(InventoryWorkloadLimits.SnapshotBatch)
                .Select(p => new { p.Id, p.Barcode, p.Name, p.Stock, p.Cost, p.Price }).ToListAsync();
            if (batch.Count == 0) throw new InvalidOperationException("La captura no se completó. No se guardó ningún registro parcial.");
            foreach (var p in batch)
                db.InventorySnapshotItems.Add(new InventorySnapshotItemEntity
                {
                    SnapshotId = snapshot.Id,
                    ProductId = p.Id,
                    Barcode = p.Barcode ?? "",
                    Name = p.Name,
                    Quantity = (decimal)p.Stock,
                    CostPrice = p.Cost,
                    SellingPrice = p.Price
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            captured += batch.Count;
            after = batch[^1].Id;
        }
        await tx.CommitAsync();
        return snapshot;
    }
    public async Task<List<InventorySnapshotEntity>> GetSnapshotsAsync(int page = 0)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await factory.CreateDbContextAsync();
        await RequireAsync(db);
        return await db.InventorySnapshots.AsNoTracking().OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id)
            .Skip(checked(page * 100)).Take(100).ToListAsync();
    }
    public async Task<InventorySnapshotEntity?> GetSnapshotDetailsAsync(string snapshotId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await RequireAsync(db);
        var header = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == snapshotId);
        if (header is null) return null;
        var rows = await db.InventorySnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshotId)
            .OrderBy(i => i.ProductId).ThenBy(i => i.Id).Take(InventoryWorkloadLimits.PrintableRows + 1).ToListAsync();
        if (rows.Count > InventoryWorkloadLimits.PrintableRows)
            throw new InvalidOperationException("La captura requiere consulta e impresión por páginas. No se devuelve un detalle parcial como si fuera completo.");
        header.Items = rows;
        return header;
    }
    public async Task<InventorySnapshotPage?> GetSnapshotPageAsync(string snapshotId, int page, int size = 100)
    {
        if (page < 0 || size is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(page));
        int offset = checked(page * size);
        await using var db = await factory.CreateDbContextAsync();
        await RequireAsync(db);
        var header = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == snapshotId);
        if (header is null) return null;
        var rows = await db.InventorySnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshotId)
            .OrderBy(i => i.ProductId).ThenBy(i => i.Id).Skip(offset).Take(size + 1).ToListAsync();
        header.Items = rows.Take(size).ToList();
        return new(header, rows.Count > size);
    }
}
