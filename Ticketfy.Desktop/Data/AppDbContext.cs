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
    public DbContextOptions<AppDbContext> Options { get; }
    public DbSet<CommandReceiptEntity> CommandReceipts => Set<CommandReceiptEntity>();
    public DbSet<HistoricalBalanceReconciliationEntity> HistoricalBalanceReconciliations => Set<HistoricalBalanceReconciliationEntity>();
    public DbSet<IncomingTransferIntentEntity> IncomingTransferIntents => Set<IncomingTransferIntentEntity>();
    public DbSet<IncomingTransferReceiptEntity> IncomingTransferReceipts => Set<IncomingTransferReceiptEntity>();
    public DbSet<TenderConfirmationEntity> TenderConfirmations => Set<TenderConfirmationEntity>();
    public DbSet<RefundSettlementEntity> RefundSettlements => Set<RefundSettlementEntity>();
    public DbSet<SalePaymentEntity> SalePayments => Set<SalePaymentEntity>();
    public DbSet<SaleLineEntity> SaleLines => Set<SaleLineEntity>();
    public DbSet<ReturnLineEntity> ReturnLines => Set<ReturnLineEntity>();
    public DbSet<PaymentRefundEntity> PaymentRefunds => Set<PaymentRefundEntity>();
    public DbSet<InventoryMovementEntity> InventoryMovements => Set<InventoryMovementEntity>();
    public DbSet<AccountEntryEntity> AccountEntries => Set<AccountEntryEntity>();
    public DbSet<SupplierPaymentEntity> SupplierPayments => Set<SupplierPaymentEntity>();
    public DbSet<SupplierReturnEntity> SupplierReturns => Set<SupplierReturnEntity>();
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

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) => Options = options;

    private void ValidatePostedRecords()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries().Any(e => e.Entity is IImmutableRecord
            && e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Los registros publicados son inmutables; registre una operación compensatoria.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidatePostedRecords();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidatePostedRecords();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<TenderConfirmationEntity>().HasIndex(e => new { e.Kind, e.ExternalReference }).IsUnique();
        modelBuilder.Entity<IncomingTransferReceiptEntity>().HasIndex(e => e.ExternalReference).IsUnique();
        modelBuilder.Entity<IncomingTransferReceiptEntity>().HasIndex(e => e.RefundReference).IsUnique().HasFilter("\"RefundReference\" IS NOT NULL");
        modelBuilder.Entity<HistoricalBalanceReconciliationEntity>().HasIndex(e => new { e.Scope, e.TargetId });
        modelBuilder.Entity<RefundSettlementEntity>().HasIndex(e => e.OperationId).IsUnique();
        modelBuilder.Entity<RefundSettlementEntity>().HasIndex(e => e.ExternalReference).IsUnique();
        modelBuilder.Entity<RefundSettlementEntity>().HasOne<PaymentRefundEntity>().WithMany().HasForeignKey(e => e.RefundId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductEntity>().HasIndex(p => p.Barcode).IsUnique().HasFilter("\"barcode\" IS NOT NULL AND \"barcode\" <> ''");
        modelBuilder.Entity<CustomerEntity>().HasIndex(p => p.CustomerCode).IsUnique().HasFilter("\"customer_code\" <> ''");
        modelBuilder.Entity<GiftcardEntity>().HasIndex(p => p.CardNumber).IsUnique();
        modelBuilder.Entity<PurchaseEntity>().HasIndex(p => new { p.SupplierId, p.InvoiceNumber }).IsUnique().HasFilter("\"InvoiceNumber\" <> '' AND \"LedgerVersion\" > 0");
        modelBuilder.Entity<ShiftEntity>().HasIndex(s => s.RegisterId).IsUnique().HasFilter("\"isOpen\" = 1 AND \"RegisterId\" IS NOT NULL");
        modelBuilder.Entity<ShiftEntity>().HasIndex(s => s.OpenOperationId).IsUnique();
        modelBuilder.Entity<CashupEntity>().HasIndex(s => s.OperationId).IsUnique();
        modelBuilder.Entity<ExpenseEntity>().HasIndex(s => s.OperationId).IsUnique();
        modelBuilder.Entity<PurchaseEntity>().HasIndex(s => s.OperationId).IsUnique();
        modelBuilder.Entity<SalePaymentEntity>().HasIndex(s => new { s.OperationId, s.Kind }).IsUnique();
        modelBuilder.Entity<SalePaymentEntity>().HasIndex(s => s.ExternalReference).IsUnique().HasFilter("\"ExternalReference\" IS NOT NULL");
        modelBuilder.Entity<SalePaymentEntity>().HasOne<SaleEntity>().WithMany().HasForeignKey(s => s.SaleId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SaleLineEntity>().HasIndex(s => new { s.SaleId, s.LineNumber }).IsUnique();
        modelBuilder.Entity<SaleLineEntity>().HasOne<SaleEntity>().WithMany().HasForeignKey(s => s.SaleId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ReturnLineEntity>().HasOne<SaleLineEntity>().WithMany().HasForeignKey(s => s.SaleLineId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ReturnLineEntity>().HasOne<ReturnEntity>().WithMany().HasForeignKey(s => s.ReturnId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentRefundEntity>().HasOne<SalePaymentEntity>().WithMany().HasForeignKey(s => s.PaymentId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentRefundEntity>().HasIndex(s => new { s.OperationId, s.PaymentId }).IsUnique();
        modelBuilder.Entity<InventoryMovementEntity>().HasIndex(s => new { s.ProductId, s.CreatedAtUtc });
        modelBuilder.Entity<AccountEntryEntity>().HasIndex(s => new { s.AccountKind, s.AccountId, s.CreatedAtUtc });
        modelBuilder.Entity<AccountEntryEntity>().HasIndex(s => new { s.OperationId, s.AccountKind, s.AccountId, s.DocumentId, s.EntryType }).IsUnique();
        modelBuilder.Entity<SupplierPaymentEntity>().HasIndex(s => s.OperationId).IsUnique();
        modelBuilder.Entity<SupplierReturnEntity>().HasIndex(s => s.OperationId).IsUnique();
        modelBuilder.Entity<SaleLineEntity>().Property(s => s.Quantity).HasConversion<SqliteQuantityConverter>();
        modelBuilder.Entity<ReturnLineEntity>().Property(s => s.Quantity).HasConversion<SqliteQuantityConverter>();
        modelBuilder.Entity<SupplierReturnEntity>().Property(s => s.Quantity).HasConversion<SqliteQuantityConverter>();
        modelBuilder.Entity<InventoryMovementEntity>().Property(s => s.Quantity).HasConversion<SqliteQuantityConverter>();
        modelBuilder.Entity<InventoryMovementEntity>().Property(s => s.StockBefore).HasConversion<SqliteQuantityConverter>();
        modelBuilder.Entity<InventoryMovementEntity>().Property(s => s.StockAfter).HasConversion<SqliteQuantityConverter>();
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

public sealed class SqliteQuantityConverter() : ValueConverter<decimal, long>(
    v => Convert.ToInt64(Math.Round(v, 3, MidpointRounding.ToEven) * 1000m), v => v / 1000m);

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
