using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Avalonia.Headless.XUnit;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.Views;
using Ticketfy.Views.Dialogs;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private sealed class PurchaseWithFailedStatement(IPurchaseService inner) : IPurchaseService
    {
        public Task<List<PurchaseDto>> GetAllAsync() => inner.GetAllAsync();
        public Task<List<PurchaseDto>> GetPageAsync(int page, int pageSize = 100, string? search = null) => inner.GetPageAsync(page, pageSize, search);
        public Task<PurchaseDto?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
        public Task<PurchaseDto> RegisterPurchaseAsync(PurchaseDto dto) => inner.RegisterPurchaseAsync(dto);
        public Task PayAsync(string purchaseId, decimal amount, string method, string? reference, string operationId)
            => inner.PayAsync(purchaseId, amount, method, reference, operationId);
        public Task<AccountStatementPageDto> GetStatementPageAsync(string supplierId, int page = 0, int size = 100, LedgerReadCut? cut = null)
            => throw new InvalidOperationException("Synthetic post-commit read failure");
    }

    [AvaloniaFact]
    public async Task Ui_SupplierPaymentCommittedBeforeStatementFailure_IsSuccessfulAndCannotPayAgainWithClearedAmount()
    {
        var factory = new TestDbContextFactory(_options);
        var purchases = new PurchaseService(factory, _actor);
        var purchase = await purchases.RegisterPurchaseAsync(new PurchaseDto("PAY-UI", "S", "Proveedor", "F-UI", "", 0m, "",
            [new("L-UI", "PAY-UI", "P", "Producto", 4m, 3, 12m)], PaymentMethod: "Efectivo"));
        var vm = new CommercialControlViewModel(ProductsService(), CustomersService(), new SupplierService(factory, _actor),
            new PurchaseWithFailedStatement(purchases), new ShiftService(factory, _actor), new GiftcardService(factory, _actor),
            new CommercialReconciliationService(factory, _actor), factory, _actor)
        { Purchase = purchase, Amount = 5m };
        await vm.PaySupplierCommand.ExecuteAsync(null);
        Assert.Equal(0m, vm.Amount);
        Assert.Contains("Operación registrada", vm.Message);
        Assert.Contains("Pago registrado", vm.Summary);
        Assert.Empty(vm.Rows);
        await vm.PaySupplierCommand.ExecuteAsync(null);
        await using var db = Db();
        Assert.Equal(5m, (await db.SupplierPayments.SingleAsync()).Amount);
        Assert.Equal(7m, (await purchases.GetStatementPageAsync("S")).Balance);
    }

    private async Task SeedPagedLedgersAsync()
    {
        await using var db = Db();
        for (int i = 1; i <= 254; i++)
        {
            foreach (var kind in new[] { AccountKind.Customer, AccountKind.Supplier })
                db.AccountEntries.Add(new AccountEntryEntity
                {
                    Id = $"{kind}-{i:D4}",
                    AccountKind = kind,
                    AccountId = kind == AccountKind.Customer ? "C" : "S",
                    Amount = 1.01m,
                    OperationId = $"{kind}-{i}",
                    DocumentId = "D-" + i,
                    EntryType = "TEST",
                    CreatedAtUtc = "2030-01-01T00:00:00Z"
                });
            db.InventoryMovements.Add(new InventoryMovementEntity
            {
                Id = $"I-{i:D4}",
                ProductId = "P",
                Quantity = 1m,
                StockBefore = i - 1,
                StockAfter = i,
                UnitCost = 3m,
                Reason = "TEST",
                CreatedAtUtc = "2030-01-01T00:00:00Z"
            });
        }
        (await db.Customers.SingleAsync()).Debt = 256.54m;
        (await db.Products.SingleAsync()).Stock = 254;
        db.SaveChanges(); // Exercise synchronous allocation too, without a caller-owned transaction.
    }

    [Fact]
    public async Task LedgerPages_PreserveFullBalanceAndVisibilityCutWhileNewBackdatedEntriesArrive()
    {
        await SeedPagedLedgersAsync();
        var customers = CustomersService();
        var purchases = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var products = ProductsService();
        var first = await customers.GetStatementPageAsync("C");
        Assert.Equal(254, first.TotalCount);
        Assert.Equal(100, first.Entries.Count);
        Assert.Equal(256.54m, first.Balance);
        Assert.Equal(155.54m, first.OpeningBalance);
        Assert.False(first.HasUnreconciledHistory);
        Assert.True(first.HasMore);
        Assert.Equal("Customer-0155", first.Entries[0].Id);
        var supplier = await purchases.GetStatementPageAsync("S");
        Assert.Equal(first.Balance, supplier.Balance);
        var stock = await products.GetKardexPageAsync("P");
        Assert.Equal(254m, stock.LedgerStock);
        Assert.False(stock.HasUnreconciledHistory);
        await using (var db = Db())
        {
            db.AccountEntries.Add(new AccountEntryEntity
            {
                AccountKind = AccountKind.Customer,
                AccountId = "C",
                Amount = 2m,
                OperationId = "LATER",
                DocumentId = "LATER",
                EntryType = "TEST",
                CreatedAtUtc = "2000-01-01T00:00:00Z"
            });
            db.InventoryMovements.Add(new InventoryMovementEntity
            {
                ProductId = "P",
                Quantity = 3m,
                StockBefore = 254,
                StockAfter = 257,
                Reason = "TEST",
                CreatedAtUtc = "2000-01-01T00:00:00Z"
            });
            (await db.Customers.SingleAsync()).Debt += 2m;
            (await db.Products.SingleAsync()).Stock = 257;
            await db.SaveChangesAsync();
        }
        var second = await customers.GetStatementPageAsync("C", 1, cut: first.Cut);
        Assert.True(second.HasNewerEntries);
        Assert.Equal(first.Balance, second.Balance);
        Assert.Equal(258.54m, second.RecordedBalance);
        Assert.Equal(54.54m, second.OpeningBalance);
        Assert.False(second.HasUnreconciledHistory);
        Assert.Empty(first.Entries.Select(e => e.Id).Intersect(second.Entries.Select(e => e.Id)));
        var last = await customers.GetStatementPageAsync("C", 2, cut: first.Cut);
        Assert.Equal(54, last.Entries.Count);
        Assert.Equal(0m, last.OpeningBalance);
        Assert.False(last.HasMore);
        var olderStock = await products.GetKardexPageAsync("P", 1, cut: stock.Cut);
        Assert.True(olderStock.HasNewerEntries);
        Assert.Equal(254m, olderStock.LedgerStock);
        Assert.Equal(257m, olderStock.RecordedStock);
        Assert.Equal(100, olderStock.Entries.Count);
        Assert.Equal(258.54m, (await customers.GetStatementPageAsync("C")).Balance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => customers.GetStatementAsync("C"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => purchases.GetStatementAsync("S"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.GetKardexAsync("P"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => purchases.GetStatementPageAsync("S", cut: first.Cut));
        await Assert.ThrowsAsync<InvalidOperationException>(() => customers.GetStatementPageAsync("C", 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => customers.GetStatementPageAsync("C", size: 201));
        await Assert.ThrowsAsync<InvalidOperationException>(() => products.GetKardexPageAsync("P", cut: stock.Cut with { ThroughSequence = long.MaxValue }));
    }

    [Fact]
    public async Task LedgerPages_ExposeMissingBaselinesAndProtectCostsAndPermissions()
    {
        await using var db = Db();
        var stock = await ProductsService().GetKardexPageAsync("P");
        Assert.Null(stock.LedgerStock);
        Assert.Equal(20m, stock.RecordedStock);
        Assert.True(stock.HasUnreconciledHistory);
        (await db.Customers.SingleAsync()).Debt = 50m; await db.SaveChangesAsync();
        var account = await CustomersService().GetStatementPageAsync("C");
        Assert.True(account.HasUnreconciledHistory);
        Assert.Equal(0m, account.Balance);
        Assert.Equal(50m, account.RecordedBalance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CustomerService(db).GetStatementPageAsync("C"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ProductService(db).GetKardexPageAsync("P"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PurchaseService(db).GetStatementPageAsync("S"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CustomersService().GetStatementPageAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProductsService().GetKardexPageAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PurchaseService(db, _actor).GetStatementPageAsync("missing"));
        await Sales.SaveAsync(Sale("COST-PAGED"));
        (await db.Users.SingleAsync()).RoleString = "CAJERO"; await db.SaveChangesAsync();
        var redacted = await ProductsService().GetKardexPageAsync("P");
        Assert.False(redacted.CostsVisible);
        Assert.Equal(0m, Assert.Single(redacted.Entries).UnitCost);
        Assert.Equal(3m, (await db.InventoryMovements.AsNoTracking().SingleAsync()).UnitCost);
    }

    [Fact]
    public async Task LedgerPages_WalletUsesCardNumberAndPreservesPagedBalanceAndCurrentDiscrepancy()
    {
        await using var db = Db();
        var factory = new TestDbContextFactory(_options);
        var wallets = new GiftcardService(factory, _actor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GiftcardService(db).GetStatementPageAsync("WALLET"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wallets.GetStatementPageAsync("G"));
        (await db.Giftcards.SingleAsync()).Balance = 255m;
        for (int i = 0; i < 255; i++)
            db.AccountEntries.Add(new AccountEntryEntity { Id = $"W-{i:D4}", OperationId = $"W-{i:D4}", AccountKind = AccountKind.Giftcard, AccountId = "WALLET", Amount = 1m });
        await db.SaveChangesAsync();
        var first = await wallets.GetStatementPageAsync("WALLET");
        Assert.Equal(255m, first.Balance); Assert.Equal(155m, first.OpeningBalance); Assert.False(first.HasUnreconciledHistory);
        var last = await wallets.GetStatementPageAsync("WALLET", 2, cut: first.Cut);
        Assert.Equal(55, last.Entries.Count); Assert.Equal(0m, last.OpeningBalance); Assert.False(last.HasMore);
        (await db.Giftcards.SingleAsync()).Balance = 300m; await db.SaveChangesAsync();
        Assert.True((await wallets.GetStatementPageAsync("WALLET")).HasUnreconciledHistory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wallets.GetStatementPageAsync("WALLET", 3, cut: first.Cut));
        var emptyStock = await ProductsService().GetKardexPageAsync("P");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProductsService().GetKardexPageAsync("P", 1, cut: emptyStock.Cut));
    }

    [AvaloniaFact]
    public async Task Ui_CustomerStatementPages_UseOpeningBalancesAndDiscardErrorsAndDismissedReads()
    {
        await SeedPagedLedgersAsync();
        var service = CustomersService();
        var fake = new DeferredCustomers(Task.FromResult(new List<CustomerDto>()))
        { StatementRead = (page, cut) => service.GetStatementPageAsync("C", page, cut: cut) };
        var vm = new CustomerStatementDialogViewModel(fake, "C", "Cliente", "", 999m);
        await vm.InitializationTask;
        var window = Show(new CustomerStatementDialog(), vm);
        try
        {
            Assert.True(vm.HasLoaded);
            Assert.Equal(256.54m, vm.CurrentDebt);
            Assert.Equal(156.55m, vm.LedgerEntries.First().Balance);
            await vm.OlderEntriesCommand.ExecuteAsync(null);
            Assert.Equal(55.55m, vm.LedgerEntries.First().Balance);
            Assert.Equal(256.54m, vm.CurrentDebt);
            Assert.True(vm.HasRecentEntries);
            await vm.OlderEntriesCommand.ExecuteAsync(null);
            Assert.Equal(54, vm.LedgerEntries.Count);
            Assert.Equal(1.01m, vm.LedgerEntries.First().Balance);
            Assert.False(vm.HasOlderEntries);
            await vm.RecentEntriesCommand.ExecuteAsync(null);
            Assert.Equal(100, vm.LedgerEntries.Count);
            fake.StatementRead = (_, _) => Task.FromException<AccountStatementPageDto>(new InvalidOperationException("sensitive synthetic exception"));
            await vm.LoadLedgerAsync();
            Assert.Empty(vm.LedgerEntries); Assert.False(vm.HasLoaded);
            Assert.Equal("No disponible", vm.BalanceDisplay);
            Assert.DoesNotContain("sensitive", vm.Status);
            var pending = new TaskCompletionSource<AccountStatementPageDto>();
            fake.StatementRead = (_, _) => pending.Task;
            var reading = vm.LoadLedgerAsync();
            vm.OnDismissed();
            pending.SetResult(await service.GetStatementPageAsync("C"));
            await reading;
            Assert.Empty(vm.LedgerEntries); Assert.False(vm.HasLoaded); Assert.False(vm.IsBusy);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_CommercialLedgers_PageAcrossCustomersSuppliersStockAndWalletWithoutPartialTotals()
    {
        await SeedPagedLedgersAsync();
        var factory = new TestDbContextFactory(_options);
        var vm = new CommercialControlViewModel(ProductsService(), CustomersService(), new SupplierService(factory, _actor),
            new PurchaseService(factory, _actor), new ShiftService(factory, _actor), new GiftcardService(factory, _actor),
            new CommercialReconciliationService(factory, _actor), factory, _actor, Sales);
        var window = Show(new CommercialControlView(), vm);
        try
        {
            vm.Customer = await CustomersService().GetByIdAsync("C");
            await vm.CustomerStatementCommand.ExecuteAsync(null);
            Assert.Equal(100, vm.Rows.Count); Assert.Equal(156.55m, vm.Rows.First().Balance);
            Assert.Contains(256.54m.ToString("C2"), vm.Summary);
            await vm.OlderLedgerCommand.ExecuteAsync(null);
            Assert.Equal(55.55m, vm.Rows.First().Balance);
            await vm.OlderLedgerCommand.ExecuteAsync(null);
            Assert.Equal(54, vm.Rows.Count); Assert.False(vm.HasOlderLedger);
            await vm.RecentLedgerCommand.ExecuteAsync(null); Assert.Equal(100, vm.Rows.Count);
            await vm.RefreshLedgerCommand.ExecuteAsync(null); Assert.False(vm.HasRecentLedger);
            vm.Supplier = await new SupplierService(factory, _actor).GetByIdAsync("S");
            Assert.Empty(vm.Rows); Assert.False(vm.LedgerVisible);
            await vm.SupplierStatementCommand.ExecuteAsync(null);
            Assert.Equal(100, vm.Rows.Count); Assert.Equal(156.55m, vm.Rows.First().Balance);
            vm.Product = await ProductsService().GetByIdAsync("P");
            await vm.KardexCommand.ExecuteAsync(null);
            Assert.Equal(100, vm.Rows.Count); Assert.Equal(155m, vm.Rows.First().Balance);
            await vm.OlderLedgerCommand.ExecuteAsync(null); Assert.Equal(55m, vm.Rows.First().Balance);
            vm.WalletNumber = "WALLET";
            await vm.WalletStatementCommand.ExecuteAsync(null);
            Assert.Empty(vm.Rows);
            Assert.Contains("ATENCIÓN", vm.Summary); // Existing 100 is not backed by a ledger opening.
            vm.WalletNumber = "missing";
            await vm.WalletStatementCommand.ExecuteAsync(null);
            Assert.Empty(vm.Rows); Assert.False(vm.LedgerVisible);
            Assert.Contains("Monedero inexistente", vm.Message);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ui_StatementSelectionChange_DiscardsLatePageAndExposesUnverifiedOpening()
    {
        await using var db = Db();
        CommercialTestSession.OpeningDebt(db, "C", 25m);
        var page = await CustomersService().GetStatementPageAsync("C");
        var dialog = new CustomerStatementDialogViewModel(CustomersService(), "C", "Cliente", "", 0m);
        await dialog.InitializationTask;
        Assert.Contains("ATENCIÓN", dialog.Status);
        Assert.Contains("apertura migrada", dialog.Status);
        Assert.Equal(25m, dialog.CurrentDebt);
        var pending = new TaskCompletionSource<AccountStatementPageDto>();
        var customers = new DeferredCustomers(Task.FromResult(new List<CustomerDto>())) { StatementRead = (_, _) => pending.Task };
        var factory = new TestDbContextFactory(_options);
        var vm = new CommercialControlViewModel(ProductsService(), customers, new SupplierService(factory, _actor),
            new PurchaseService(factory, _actor), new ShiftService(factory, _actor), new GiftcardService(factory, _actor),
            new CommercialReconciliationService(factory, _actor), factory, _actor);
        vm.Customer = await CustomersService().GetByIdAsync("C");
        var loading = vm.CustomerStatementCommand.ExecuteAsync(null);
        Assert.False(loading.IsCompleted);
        vm.Customer = null;
        pending.SetResult(page);
        await loading;
        Assert.Empty(vm.Rows); Assert.False(vm.LedgerVisible); Assert.Empty(vm.Summary);
    }

    [Fact]
    public async Task LedgerSequenceMigration_PreservesEvidenceAndSurvivesVacuumAndReapplication()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261020044116_PersistCredentialAttemptLimits");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO account_entries (Id, AccountKind, AccountId, OperationId, DocumentId, EntryType, Amount, RegisterId, CashierUserId, CreatedAtUtc)
            VALUES ('OLD-A',0,'C','A','A','MIGRATION_OPENING',1234,'legacy','legacy','legacy-date'),
                   ('OLD-B',0,'C','B','B','TEST',-125,'legacy','legacy','2030-01-01T00:00:00Z');
            INSERT INTO inventory_movements (Id,ProductId,OperationId,DocumentId,Reason,Quantity,StockBefore,StockAfter,UnitCost,RegisterId,CashierUserId,CreatedAtUtc)
            VALUES ('OLD-I','P','I','I','MIGRATION_OPENING',1250,0,1250,300,'legacy','legacy','legacy-date');
            """);
        await migrator.MigrateAsync();
        var before = await db.AccountEntries.AsNoTracking().OrderBy(e => e.PostingSequence).ToListAsync();
        Assert.Equal(new long[] { 1, 2 }, before.Select(e => e.PostingSequence));
        Assert.Equal(11.09m, before.Sum(e => e.Amount));
        Assert.Equal("legacy-date", before.Single(e => e.Id == "OLD-A").CreatedAtUtc);
        Assert.Equal(1.25m, (await db.InventoryMovements.SingleAsync()).StockAfter);
        await db.Database.ExecuteSqlRawAsync("VACUUM;");
        await migrator.MigrateAsync();
        Assert.Equal(before.Select(e => (e.Id, e.PostingSequence)),
            (await db.AccountEntries.AsNoTracking().OrderBy(e => e.PostingSequence).ToListAsync()).Select(e => (e.Id, e.PostingSequence)));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE account_entries SET Amount=0 WHERE Id='OLD-A';"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO account_entries (Id,PostingSequence,AccountKind,AccountId,OperationId,DocumentId,EntryType,Amount,RegisterId,CashierUserId,CreatedAtUtc)
            VALUES ('UNSEQUENCED',0,0,'C','X','X','TEST',1,'test','test','2030-01-01T00:00:00Z');
            """));
        db.AccountEntries.Add(new AccountEntryEntity { AccountId = "C", OperationId = "NEW", DocumentId = "NEW", EntryType = "TEST", Amount = 1m });
        await db.SaveChangesAsync();
        Assert.Equal(3, await db.AccountEntries.MaxAsync(e => e.PostingSequence));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
