using Avalonia.Headless.XUnit;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("invalid-json")]
    [InlineData("header")]
    [InlineData("line")]
    [InlineData("payment")]
    [InlineData("nul")]
    [InlineData("total")]
    [InlineData("missing-lines")]
    [InlineData("returned")]
    [InlineData("header-tender")]
    [InlineData("unit-cost")]
    public async Task HistoryDocument_RejectsIncompleteDetailWithoutLosingHeaderOrChangingMoney(string corruption)
    {
        var sale = await Sales.SaveAsync(Sale("HISTORY-BOUNDS"));
        if (corruption == "returned") await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Prueba", operationId: "HISTORY-DAMAGE");
        await using (var db = Db())
        {
            string huge = new('é', 2 * 1024 * 1024);
            switch (corruption)
            {
                case "json": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET items = {huge}"); break;
                case "invalid-json": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET items = {"{broken"}"); break;
                case "header": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET \"CashierName\" = {huge}"); break;
                case "line": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sale_lines SET \"Name\" = {huge}"); break;
                case "payment": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sale_payments SET \"ExternalReference\" = {huge}"); break;
                case "nul": await db.Database.ExecuteSqlRawAsync("UPDATE sales SET items = CAST(CAST(items AS BLOB) || X'0078' AS TEXT)"); break;
                case "total": await db.Database.ExecuteSqlRawAsync("UPDATE sale_lines SET Total = 1"); break;
                case "header-tender": await db.Database.ExecuteSqlRawAsync("UPDATE sales SET cashAmount = 1"); break;
                case "unit-cost": await db.Database.ExecuteSqlRawAsync("UPDATE sale_lines SET UnitCost = 1"); break;
                case "missing-lines": await db.Database.ExecuteSqlRawAsync("DELETE FROM sale_lines"); break;
                case "returned":
                    await db.Database.ExecuteSqlRawAsync("UPDATE return_lines SET Quantity = 3000, Amount = 3000"); break;
            }
        }
        var before = await ReturnStateAsync();
        var detail = (await Sales.GetByIdAsync(sale.Id))!;
        Assert.False(detail.DetailsAvailable); Assert.Empty(detail.Items); Assert.Empty(detail.Payments!);
        Assert.Equal(sale.Id, detail.Id); Assert.Equal(20m, detail.Total); Assert.Contains("NO VERIFICADO", detail.PaymentStatusDisplay);
        var history = await Sales.GetHistoryPageAsync(BusinessTime.Today, BusinessTime.Today, 0);
        Assert.Equal(20m, history.Hours.Sum(h => h.GrossAmount));
        Assert.False(Assert.Single(history.Items).DetailsAvailable);
        Assert.Equal(before, await ReturnStateAsync());
    }

    [AvaloniaFact]
    public async Task HistoryDocument_PageBudgetDefersWholeDocumentsAndUiLoadsIndividualDetail()
    {
        await using (var db = Db())
        { (await db.Products.SingleAsync()).Stock = 100; await db.SaveChangesAsync(); }
        for (int i = 0; i < 20; i++) await Sales.SaveAsync(Sale("PAGE-SALE-" + i));
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sales SET items = items || {new string(' ', 600 * 1024)}");
        var history = await Sales.GetHistoryAsync(100);
        Assert.Equal(20, history.Count);
        Assert.Contains(history, s => s.DetailsAvailable);
        var pending = history.First(s => !s.DetailsAvailable);
        Assert.Contains("individual", pending.DetailWarning);
        Assert.Empty(pending.Items);
        var model = new HistoryViewModel(Sales, null!);
        await model.LoadSalesAsync();
        await model.ViewSaleDetailCommand.ExecuteAsync(pending);
        Assert.True(model.IsSaleDetailDialogOpen);
        Assert.True(model.SelectedSaleForDetail!.DetailsAvailable);
        Assert.Single(model.SelectedSaleForDetail.Items);
        model.CloseSaleDetailCommand.Execute(null); Assert.Null(model.SelectedSaleForDetail);
        model.ClearSessionData();
    }

    [Fact]
    public async Task HistoryDocument_StreamsReturnsAndBalancesWithoutHydratingHistoricalEntities()
    {
        var sale = await Sales.SaveAsync(Sale("HISTORY-CREDIT") with { CustomerId = "C", IsCredit = true, CashAmount = 0m, PaymentMethod = "Crédito" });
        await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Prueba", operationId: "HISTORY-RETURN");
        var observer = new HistoryEntitiesProbe();
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).EnableServiceProviderCaching(false).AddInterceptors(observer).Options;
        var service = new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), _actor);
        var read = (await service.GetByIdAsync(sale.Id))!;
        Assert.True(read.DetailsAvailable); Assert.Equal(10m, read.RefundedAmount); Assert.Equal(10m, read.CreditDocumentBalance);
        Assert.Equal(1d, read.Items.Single().ReturnedQuantity); Assert.Equal(0, observer.Historical);
        Assert.Equal(2, observer.MaximumPayload); // Headers carry [] rather than the JSON payload.
        Assert.Null(await service.GetByIdAsync("MISSING"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetByIdAsync("bad\0id"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetHistoryAsync(201));
    }

    private sealed class HistoryEntitiesProbe : IMaterializationInterceptor
    {
        public int Historical, MaximumPayload;
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (entity is ReturnLineEntity or AccountEntryEntity or ReturnEntity) Historical++;
            if (entity is SaleEntity sale) MaximumPayload = Math.Max(MaximumPayload, sale.ItemsJson.Length);
            return entity;
        }
    }

    [AvaloniaFact]
    public async Task HistoryDocument_LateDetailCannotReopenAfterDismissalPeriodOrLogout()
    {
        var sale = await Sales.SaveAsync(Sale("DETAIL-LATE"));
        foreach (string transition in new[] { "close", "date", "logout" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new HistoryReadGate(async sql =>
            {
                if (!sql.Contains("Sale document header", StringComparison.Ordinal)) return;
                entered.TrySetResult(); await release.Task;
            });
            var options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(gate).Options;
            var model = new HistoryViewModel(new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), _actor), null!);
            var loading = model.ViewSaleDetailCommand.ExecuteAsync(sale);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                if (transition == "close") model.CloseSaleDetailCommand.Execute(null);
                else if (transition == "date") model.StartDate = BusinessTime.Today.AddDays(-1);
                else model.ClearSessionData();
            }
            finally { release.TrySetResult(); }
            await loading;
            Assert.Null(model.SelectedSaleForDetail); Assert.False(model.IsSaleDetailDialogOpen);
            model.ClearSessionData();
        }
    }

    [AvaloniaFact]
    public async Task HistoryDocument_ReadFailureClearsOldDetailsAndPrinterRejectsUnverifiedDocuments()
    {
        var sale = await Sales.SaveAsync(Sale("DETAIL-FAILURE"));
        bool fail = false;
        var gate = new HistoryReadGate(sql =>
        {
            if (fail && sql.Contains("Sale document header", StringComparison.Ordinal)) throw new IOException("private-synthetic-error");
            return Task.CompletedTask;
        });
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(gate).Options;
        var model = new HistoryViewModel(new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), _actor), null!);
        await model.ViewSaleDetailCommand.ExecuteAsync(sale); Assert.NotNull(model.SelectedSaleForDetail);
        fail = true; await model.ViewSaleDetailCommand.ExecuteAsync(sale);
        Assert.Null(model.SelectedSaleForDetail); Assert.False(model.IsSaleDetailDialogOpen);
        Assert.Contains("No se pudo consultar", model.FeedbackMessage); Assert.DoesNotContain("private-synthetic-error", model.FeedbackMessage);
        await using var printer = new EscPosPrinterService();
        Assert.False(await printer.PrintTicketAsync(sale with { DetailsAvailable = false }));
        model.ClearSessionData();
    }

    private sealed class HistoryReadGate(Func<string, Task> onRead) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await onRead(command.CommandText); return result; }
    }
}
