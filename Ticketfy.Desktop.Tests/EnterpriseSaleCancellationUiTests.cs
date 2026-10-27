using System.Data.Common;
using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Data;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaTheory]
    [InlineData("success", "Cancelación registrada.")]
    [InlineData("refresh-failure", "Cancelación registrada.")]
    [InlineData("already-canceled", "No se registró una nueva cancelación.")]
    [InlineData("inconsistent", "No se pudo confirmar la cancelación.")]
    public async Task SaleCancellationUi_ReportsOnlyConfirmedOutcomeAndKeepsReceiptAfterReadFailure(string scenario, string prefix)
    {
        var sale = await Sales.SaveAsync(Sale("CANCEL-UI"));
        if (scenario == "already-canceled") await Sales.CancelSaleAsync(sale.Id, "Cancelación administrativa");
        if (scenario == "inconsistent")
        {
            await using var db = Db();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM sale_payments;");
        }
        var fault = new ReturnHistoryReadFailure();
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).EnableServiceProviderCaching(false).AddInterceptors(fault).Options;
        var model = new HistoryViewModel(new SaleService(new TestDbContextFactory(options), new(), new SpyAuditService(), _actor), null!);
        await model.LoadSalesAsync();
        Action<bool>? approve = null;
        model.OpenSupervisorPinRequested += (_, response) => approve = response;
        var feedback = new TaskCompletionSource<string>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.FeedbackMessage) && model.FeedbackMessage.StartsWith(prefix, StringComparison.Ordinal))
                feedback.TrySetResult(model.FeedbackMessage);
        };
        fault.Enabled = scenario == "refresh-failure";
        await model.CancelSaleCommand.ExecuteAsync(sale);
        Assert.NotNull(approve);
        approve(true);
        var message = await feedback.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(scenario == "inconsistent" ? "no entregue dinero" : "No repita", message, StringComparison.OrdinalIgnoreCase);
        if (scenario == "refresh-failure") Assert.Contains("No se pudo consultar el historial", message);
        if (scenario is "success" or "refresh-failure") Assert.Contains("conciliación bancaria", message);
        var beforeReplay = await ReturnStateAsync();
        approve(true);
        Assert.Equal(beforeReplay, await ReturnStateAsync());
        Assert.Equal(scenario == "inconsistent" ? 0 : 1, beforeReplay.Returns);
        model.ClearSessionData();
    }

    private sealed class ReturnHistoryReadFailure : DbCommandInterceptor
    {
        public bool Enabled;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("Sale history headers", StringComparison.Ordinal))
                throw new IOException("synthetic history read failure");
            return ValueTask.FromResult(result);
        }
    }
}
