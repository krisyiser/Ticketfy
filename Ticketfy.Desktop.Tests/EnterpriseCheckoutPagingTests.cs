using System.Data.Common;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task CheckoutPaging_CursorDoesNotSkipRowsWhenEarlierIntentsAreResolved()
    {
        await SeedPendingCheckoutsAsync(205);
        var first = await Sales.GetInterruptedCheckoutsPageAsync();
        Assert.Equal(205, first.TotalCount); Assert.Equal(100, first.Items.Count); Assert.NotNull(first.Next);
        await using (var db = Db())
        {
            await db.CheckoutOperations.Where(o => o.OperationId == "PENDING-0000").ExecuteUpdateAsync(
                update => update.SetProperty(o => o.State, CheckoutOperationState.Failed));
            db.CheckoutOperations.Add(new()
            { OperationId = "OTHER-REGISTER", RegisterId = "OTHER", Amount = 1000m, State = CheckoutOperationState.RequiresReconciliation });
            await db.SaveChangesAsync();
        }
        var second = await Sales.GetInterruptedCheckoutsPageAsync(after: first.Next);
        var third = await Sales.GetInterruptedCheckoutsPageAsync(after: second.Next);
        Assert.Equal(204, second.TotalCount); Assert.Equal(204, third.TotalCount);
        Assert.Equal("PENDING-0100", second.Items[0].OperationId); Assert.Equal(100, second.Items.Count);
        Assert.Equal(5, third.Items.Count); Assert.Null(third.Next);
        Assert.Equal(205, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.OperationId).Distinct().Count());
        var search = await Sales.GetInterruptedCheckoutsPageAsync(search: " 0204 ");
        Assert.Equal(1, search.TotalCount); Assert.Equal("PENDING-0204", Assert.Single(search.Items).OperationId);
        Assert.Null(search.Next);
        Assert.Empty((await Sales.GetInterruptedCheckoutsPageAsync(search: "NOT-FOUND")).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => Sales.GetInterruptedCheckoutsPageAsync(search: "OTHER", after: first.Next));
        await Assert.ThrowsAsync<ArgumentException>(() => Sales.GetInterruptedCheckoutsPageAsync(after: first.Next! with { RegisterId = "OTHER" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Sales.GetInterruptedCheckoutsPageAsync(after: first.Next! with { AfterOperationId = "" }));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(201, "")]
    [InlineData(100, "newline\n")]
    public async Task CheckoutPaging_RejectsInvalidLimitsAndFilters(int size, string search)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Sales.GetInterruptedCheckoutsPageAsync(size, search));
        await Assert.ThrowsAsync<ArgumentException>(() => Sales.GetInterruptedCheckoutsPageAsync(search: new string('x', 129)));
    }

    [Fact]
    public async Task CheckoutPaging_RequiresPermissionAndBoundsSqlWithoutReadingPayloads()
    {
        await SeedPendingCheckoutsAsync(1001);
        var observer = new CheckoutReadObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        var service = new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), _actor);
        var result = await service.GetInterruptedCheckoutsPageAsync(17);
        Assert.Equal(1001, result.TotalCount); Assert.Equal(17, result.Items.Count);
        Assert.False(observer.ReadsPayload); Assert.False(observer.UnboundedList);
        Assert.Contains(18, observer.Limits);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService()).GetInterruptedCheckoutsPageAsync());
        await using var db = Db();
        (await db.Users.SingleAsync()).RoleString = "CAJERO"; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetInterruptedCheckoutsPageAsync());
    }

    [Fact]
    public async Task CheckoutPaging_ChangedSessionCannotReceivePreviousOperatorsQuery()
    {
        await SeedPendingCheckoutsAsync(1);
        var session = new SessionManager(); var engine = new UserRolePermissionEngine();
        Ticketfy.Core.Models.UserModel replacement;
        await using (var db = Db())
        {
            session.StartSession(engine.MapToModel(await db.Users.SingleAsync()));
            var otherId = Guid.NewGuid();
            CommercialTestSession.Create(db, false, userId: otherId);
            replacement = engine.MapToModel(await db.Users.SingleAsync(u => u.Id == otherId));
        }
        bool changed = false;
        var observer = new CheckoutReadObserver(command =>
        {
            if (!changed && command.CommandText.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase))
            { changed = true; session.StartSession(replacement); }
            return Task.CompletedTask;
        });
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options);
        var service = new SaleService(factory, new(), new SpyAuditService(), new CommercialOperationContext(session, engine, "TEST-REGISTER"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetInterruptedCheckoutsPageAsync());
        Assert.True(changed);
    }

    [Fact]
    public async Task CheckoutPaging_CountAndHeadersShareOneReadSnapshot()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, actor) =>
        {
            await using (var db = new AppDbContext(options))
            {
                db.CheckoutOperations.Add(new() { OperationId = "BEFORE-PAGE", RegisterId = "TEST-REGISTER", Amount = 20m });
                await db.SaveChangesAsync();
            }
            bool wrote = false;
            var observer = new CheckoutReadObserver(async command =>
            {
                if (wrote || !command.CommandText.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase)) return;
                wrote = true;
                await using var db = new AppDbContext(options);
                db.CheckoutOperations.Add(new() { OperationId = "DURING-PAGE", RegisterId = "TEST-REGISTER", Amount = 30m });
                await db.SaveChangesAsync();
            });
            var service = new SaleService(new TestDbContextFactory(
                new DbContextOptionsBuilder<AppDbContext>(options).AddInterceptors(observer).Options), new(), new SpyAuditService(), actor);
            var page = await service.GetInterruptedCheckoutsPageAsync();
            Assert.True(wrote); Assert.Equal(1, page.TotalCount);
            Assert.Equal("BEFORE-PAGE", Assert.Single(page.Items).OperationId);
            var refreshed = await service.GetInterruptedCheckoutsPageAsync();
            Assert.Equal(2, refreshed.TotalCount); Assert.Equal(2, refreshed.Items.Count);
        });
    }

    [AvaloniaFact]
    public async Task CheckoutPaging_BoundControlTraversesSearchesAndClearsOldSelection()
    {
        await SeedPendingCheckoutsAsync(205);
        var model = EnterpriseControl(); var view = new CommercialControlView(); var window = Show(view, model);
        try
        {
            await model.RefreshCommand.ExecuteAsync(null);
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Recuperar cobros"));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }); window.UpdateLayout();
            Assert.Contains("205", model.PendingCheckoutPageLabel);
            model.PendingCheckout = model.PendingCheckouts[0]; model.CheckoutFundsVerified = true;
            var next = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Command == model.NextPendingCheckoutsCommand);
            Assert.True(next.IsEffectivelyEnabled);
            await model.NextPendingCheckoutsCommand.ExecuteAsync(null);
            Assert.Null(model.PendingCheckout); Assert.False(model.CheckoutFundsVerified);
            Assert.Equal("PENDING-0100", model.PendingCheckouts[0].OperationId); Assert.True(model.HasPreviousPendingCheckouts);
            await model.NextPendingCheckoutsCommand.ExecuteAsync(null);
            Assert.Equal(5, model.PendingCheckouts.Count); Assert.False(model.HasNextPendingCheckouts);
            await model.PreviousPendingCheckoutsCommand.ExecuteAsync(null);
            Assert.Equal("PENDING-0100", model.PendingCheckouts[0].OperationId);
            var search = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), b => b.Watermark?.ToString() == "Buscar referencia local del cobro");
            search.Text = "0204";
            Assert.Empty(model.PendingCheckouts); Assert.Empty(model.PendingCheckoutPageLabel);
            await model.SearchPendingCheckoutsCommand.ExecuteAsync(null);
            Assert.Equal("PENDING-0204", Assert.Single(model.PendingCheckouts).OperationId);
            Assert.False(model.HasNextPendingCheckouts); Assert.False(model.HasPreviousPendingCheckouts);
            model.ClearDirectoryData(); Assert.Empty(model.PendingCheckouts); Assert.Null(model.PendingCheckout);
        }
        finally { window.Close(); }
    }

    private sealed class CheckoutReadObserver(Func<DbCommand, Task>? afterRead = null) : DbCommandInterceptor
    {
        public bool ReadsPayload, UnboundedList;
        public List<int> Limits { get; } = [];
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"checkout_operations\"", StringComparison.Ordinal))
            {
                ReadsPayload |= command.CommandText.Contains("\"sale_payload\"", StringComparison.Ordinal);
                string sql = command.CommandText.TrimStart();
                bool scalar = sql.StartsWith("SELECT COUNT(", StringComparison.OrdinalIgnoreCase) ||
                    sql.StartsWith("SELECT MAX(", StringComparison.OrdinalIgnoreCase) ||
                    sql.StartsWith("SELECT EXISTS", StringComparison.OrdinalIgnoreCase);
                UnboundedList |= !scalar && !command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase);
                if (!scalar) Limits.AddRange(command.Parameters.Cast<DbParameter>().Where(p => p.Value is int).Select(p => (int)p.Value!));
                if (afterRead is not null) await afterRead(command);
            }
            return result;
        }
    }
}
