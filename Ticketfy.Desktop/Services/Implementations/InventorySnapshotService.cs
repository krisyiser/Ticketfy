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

namespace Ticketfy.Services.Implementations;

public class InventorySnapshotService : IInventorySnapshotService
{
    public async Task<InventorySnapshotEntity> CreateSnapshotAsync(string notes)
    {
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={Ticketfy.Services.Security.SecurityManager.GetMasterKey()};")
                .Options;

            using var context = new AppDbContext(options);

            var products = await context.Products.AsNoTracking().ToListAsync();

            var snapshot = new InventorySnapshotEntity
            {
                Notes = notes,
                TotalItems = products.Count,
                TotalValue = products.Sum(p => (decimal)p.Cost * (decimal)p.Stock)
            };

            foreach (var p in products)
            {
                snapshot.Items.Add(new InventorySnapshotItemEntity
                {
                    ProductId = p.Id,
                    Barcode = p.Barcode ?? string.Empty,
                    Name = p.Name,
                    Quantity = (decimal)p.Stock,
                    CostPrice = (decimal)p.Cost,
                    SellingPrice = (decimal)p.Price
                });
            }

            context.InventorySnapshots.Add(snapshot);
            await context.SaveChangesAsync();

            Log.Information($"Inventory snapshot created successfully: {snapshot.Id} with {snapshot.TotalItems} items.");
            return snapshot;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create inventory snapshot.");
            throw;
        }
    }

    public async Task<List<InventorySnapshotEntity>> GetSnapshotsAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={Ticketfy.Services.Security.SecurityManager.GetMasterKey()};")
            .Options;
        using var context = new AppDbContext(options);
        return await context.InventorySnapshots
            .Include(s => s.Items)
            .AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<InventorySnapshotEntity?> GetSnapshotDetailsAsync(string snapshotId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={Ticketfy.Services.Security.SecurityManager.GetMasterKey()};")
            .Options;
        using var context = new AppDbContext(options);
        return await context.InventorySnapshots
            .Include(s => s.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == snapshotId);
    }
}
