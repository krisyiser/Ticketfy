using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Ticketfy.Data.Entities;
using System;
using Ticketfy.Core.Configuration;

namespace Ticketfy.Data;

public class SqliteDecimalConverter : ValueConverter<decimal, long>
{
    public SqliteDecimalConverter() : base(
        v => Convert.ToInt64(Math.Round(v, 2, MidpointRounding.ToEven) * 100m),
        v => Convert.ToDecimal(v) / 100m)
    { }
}

public class AppDbContext : DbContext
{
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();
    public DbSet<PromotionEntity> Promotions => Set<PromotionEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<SaleEntity> Sales => Set<SaleEntity>();
    public DbSet<FiscalClientEntity> FiscalClients => Set<FiscalClientEntity>();
    public DbSet<ShiftEntity> Shifts => Set<ShiftEntity>();
    public DbSet<CustomerPaymentEntity> CustomerPayments => Set<CustomerPaymentEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<SupplierEntity> Suppliers => Set<SupplierEntity>();
    public DbSet<PurchaseEntity> Purchases => Set<PurchaseEntity>();
    public DbSet<PurchaseItemEntity> PurchaseItems => Set<PurchaseItemEntity>();
    public DbSet<ExpenseEntity> Expenses => Set<ExpenseEntity>();
    public DbSet<ParkedOrderEntity> ParkedOrders => Set<ParkedOrderEntity>();
    public DbSet<ItemKitEntity> ItemKits => Set<ItemKitEntity>();
    public DbSet<ItemKitItemEntity> ItemKitItems => Set<ItemKitItemEntity>();
    public DbSet<GiftcardEntity> Giftcards => Set<GiftcardEntity>();
    public DbSet<CashupEntity> Cashups => Set<CashupEntity>();
    public DbSet<ProductAttributeEntity> ProductAttributes => Set<ProductAttributeEntity>();
    public DbSet<ShiftNoteEntity> ShiftNotes => Set<ShiftNoteEntity>();
    public DbSet<ShiftMovementEntity> ShiftMovements => Set<ShiftMovementEntity>();
    public DbSet<SystemAlertEntity> SystemAlerts => Set<SystemAlertEntity>();
    public DbSet<ReturnEntity> Returns => Set<ReturnEntity>();
    public DbSet<AttendanceEntity> Attendances => Set<AttendanceEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<FolioSequenceEntity> FolioSequences => Set<FolioSequenceEntity>();
    public DbSet<CoOccurrenceEntity> CoOccurrences => Set<CoOccurrenceEntity>();
    public DbSet<InventorySnapshotEntity> InventorySnapshots => Set<InventorySnapshotEntity>();
    public DbSet<InventorySnapshotItemEntity> InventorySnapshotItems => Set<InventorySnapshotItemEntity>();
    public DbSet<CheckoutOperationEntity> CheckoutOperations => Set<CheckoutOperationEntity>();
    public DbSet<TransactionJournalEntryEntity> TransactionJournal => Set<TransactionJournalEntryEntity>();
    public DbSet<FiscalOperationEntity> FiscalOperations => Set<FiscalOperationEntity>();
    public DbSet<PaymentTerminalOperationEntity> PaymentTerminalOperations => Set<PaymentTerminalOperationEntity>();
    public DbSet<IntegrationWebhookEventEntity> IntegrationWebhookEvents => Set<IntegrationWebhookEventEntity>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<CategoryEntity>().HasKey(c => c.Id);
        modelBuilder.Entity<ProductEntity>().HasKey(p => p.Id);
        modelBuilder.Entity<CustomerEntity>().HasKey(c => c.Id);
        modelBuilder.Entity<PromotionEntity>().HasKey(p => p.Id);
        modelBuilder.Entity<SettingEntity>().HasKey(s => s.Key);
        modelBuilder.Entity<SaleEntity>().HasKey(s => s.Id);
        modelBuilder.Entity<FiscalClientEntity>().HasKey(f => f.Id);
        modelBuilder.Entity<ShiftEntity>().HasKey(s => s.Id);
        modelBuilder.Entity<CustomerPaymentEntity>().HasKey(cp => cp.Id);
        modelBuilder.Entity<UserEntity>().HasKey(u => u.Id);
        modelBuilder.Entity<SupplierEntity>().HasKey(s => s.Id);
        modelBuilder.Entity<PurchaseEntity>().HasKey(p => p.Id);
        modelBuilder.Entity<PurchaseItemEntity>().HasKey(pi => pi.Id);
        modelBuilder.Entity<ExpenseEntity>().HasKey(e => e.Id);
        modelBuilder.Entity<ParkedOrderEntity>().HasKey(po => po.Id);
        modelBuilder.Entity<ItemKitEntity>().HasKey(ik => ik.Id);
        modelBuilder.Entity<ItemKitItemEntity>().HasKey(iki => iki.Id);
        modelBuilder.Entity<GiftcardEntity>().HasKey(g => g.Id);
        modelBuilder.Entity<CashupEntity>().HasKey(c => c.Id);
        modelBuilder.Entity<ProductAttributeEntity>().HasKey(pa => pa.Id);
        modelBuilder.Entity<ShiftNoteEntity>().HasKey(sn => sn.Id);
        modelBuilder.Entity<AttendanceEntity>().HasKey(a => a.Id);
        modelBuilder.Entity<FolioSequenceEntity>().HasKey(f => f.DatePrefix);
        modelBuilder.Entity<CoOccurrenceEntity>().HasKey(c => new { c.ProductoA, c.ProductoB });
        modelBuilder.Entity<CheckoutOperationEntity>().HasKey(c => c.OperationId);
        modelBuilder.Entity<TransactionJournalEntryEntity>().HasKey(e => e.Id);
        modelBuilder.Entity<FiscalOperationEntity>().HasKey(e => e.Id);
        modelBuilder.Entity<PaymentTerminalOperationEntity>().HasKey(e => e.Id);
        modelBuilder.Entity<IntegrationWebhookEventEntity>().HasKey(e => e.Id);

        modelBuilder.Entity<SaleEntity>()
            .HasIndex(s => s.OperationId)
            .IsUnique();
        modelBuilder.Entity<CustomerPaymentEntity>()
            .HasIndex(p => p.OperationId)
            .IsUnique();
        modelBuilder.Entity<ReturnEntity>()
            .HasIndex(r => r.OperationId)
            .IsUnique();
        modelBuilder.Entity<ProductEntity>()
            .Property(p => p.StockVersion)
            .IsConcurrencyToken();
        modelBuilder.Entity<TransactionJournalEntryEntity>()
            .HasIndex(e => new { e.OperationId, e.EventType })
            .IsUnique();
        modelBuilder.Entity<FiscalOperationEntity>()
            .HasIndex(e => e.IdempotencyKey)
            .IsUnique();
        modelBuilder.Entity<FiscalOperationEntity>()
            .HasIndex(e => new { e.Status, e.NextAttemptAtUtc });
        modelBuilder.Entity<PaymentTerminalOperationEntity>()
            .HasIndex(e => e.ExternalReference)
            .IsUnique();
        modelBuilder.Entity<PaymentTerminalOperationEntity>()
            .HasIndex(e => e.IdempotencyKey)
            .IsUnique();
        modelBuilder.Entity<PaymentTerminalOperationEntity>()
            .HasIndex(e => e.ProviderOrderId)
            .IsUnique();
        modelBuilder.Entity<IntegrationWebhookEventEntity>()
            .HasIndex(e => new { e.Provider, e.EventId })
            .IsUnique();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            configurationBuilder
                .Properties<decimal>()
                .HaveConversion<SqliteDecimalConverter>();
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (!optionsBuilder.IsConfigured)
        {
            AppDataPaths.EnsureRequiredDirectories();
            string securePassword = Ticketfy.Services.Security.SecurityManager.GetMasterKey();
            optionsBuilder.UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={securePassword};Cache=Shared;Mode=ReadWriteCreate;");
        }
    }
}

public class AppDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        // Design-time tooling only needs the model. Keeping its scratch database beside
        // the build output avoids touching the operator's production data directory.
        var designDatabase = System.IO.Path.Combine(AppContext.BaseDirectory, "ticketfy.design.db");
        optionsBuilder.UseSqlite($"Data Source={designDatabase};Mode=ReadWriteCreate;");
        return new AppDbContext(optionsBuilder.Options);
    }
}
