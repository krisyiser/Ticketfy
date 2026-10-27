using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Operations;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceTerminal_BlockedHealthPreventsNewAndRepeatedChargeAndCancellation(bool existing)
    {
        if (existing) await SeedHealthTerminalIntent();
        var health = new MaintenanceHealth(); UseMaintenanceGuard(health);
        var handler = new HealthTerminalHandler();
        using var client = new HttpClient(handler);
        var terminal = new MercadoPagoTerminalService(client, new HealthTerminalSettings(),
            contextFactory: new TestDbContextFactory(_options),
            accessTokenProvider: () => throw new InvalidOperationException("Must not read secrets while blocked"),
            maintenance: health, operations: _actor);
        var result = await terminal.ProcessPaymentAsync(20m, "HEALTH-CARD", default);
        Assert.False(result.IsSuccess); Assert.Contains("escrituras", result.ErrorMessage);
        Assert.False((await terminal.CancelPaymentIntentAsync("HEALTH-CARD")).IsSuccess);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(existing, await terminal.RequiresReconciliationAsync("HEALTH-CARD"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceTerminal_FailureAfterReadingExistingIntentStopsProviderWrite(bool cancel)
    {
        await SeedHealthTerminalIntent();
        await PrepareHealthCheckout();
        if (cancel)
        {
            await using var db = Db();
            await db.PaymentTerminalOperations.ExecuteUpdateAsync(p => p.SetProperty(x => x.ProviderOrderId, "ORD-SYNTHETIC"));
        }
        var health = new MaintenanceHealth(); health.Succeeded(MaintenanceTask.Audit); UseMaintenanceGuard(health);
        var handler = new HealthTerminalHandler();
        using var client = new HttpClient(handler);
        var terminal = new MercadoPagoTerminalService(client, new HealthTerminalSettings(),
            contextFactory: new TestDbContextFactory(_options),
            accessTokenProvider: () => { health.Failed(MaintenanceTask.Audit); return Task.FromResult<string?>("TEST_TOKEN"); },
            maintenance: health, operations: _actor);
        var result = cancel ? await terminal.CancelPaymentIntentAsync("HEALTH-CARD")
            : await terminal.ProcessPaymentAsync(20m, "HEALTH-CARD", default);
        Assert.False(result.IsSuccess); Assert.Contains("escrituras", result.ErrorMessage); Assert.Equal(0, handler.Calls);
        Assert.True(await terminal.RequiresReconciliationAsync("HEALTH-CARD"));
    }

    [Fact]
    public async Task MaintenanceTerminal_FailureAfterProviderAcceptedRetainsOriginalIntentAndDoesNotClaimRejection()
    {
        await PrepareHealthCheckout();
        var health = new MaintenanceHealth(); health.Succeeded(MaintenanceTask.Audit); UseMaintenanceGuard(health);
        var handler = new HealthTerminalHandler(() => health.Failed(MaintenanceTask.Audit));
        using var client = new HttpClient(handler);
        var terminal = new MercadoPagoTerminalService(client, new HealthTerminalSettings(),
            contextFactory: new TestDbContextFactory(_options), accessTokenProvider: () => Task.FromResult<string?>("TEST_TOKEN"),
            maintenance: health, operations: _actor);
        var result = await terminal.ProcessPaymentAsync(20m, "HEALTH-CARD", default);
        Assert.False(result.IsSuccess); Assert.Contains("no lo repita", result.ErrorMessage);
        Assert.Equal(1, handler.Calls); Assert.True(await terminal.RequiresReconciliationAsync("HEALTH-CARD"));
        await using var db = Db();
        var pending = await db.PaymentTerminalOperations.SingleAsync();
        Assert.Equal("HEALTH-CARD", pending.ExternalReference);
        Assert.Equal(IntegrationOperationStatus.Pending, pending.Status);
        Assert.False(string.IsNullOrWhiteSpace(pending.IdempotencyKey));
        Assert.False((await terminal.ProcessPaymentAsync(20m, "HEALTH-CARD", default)).IsSuccess);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(IntegrationOperationStatus.Pending, true)]
    [InlineData(IntegrationOperationStatus.Processing, true)]
    [InlineData(IntegrationOperationStatus.WaitingRetry, true)]
    [InlineData(IntegrationOperationStatus.Completed, true)]
    [InlineData(IntegrationOperationStatus.NeedsReconciliation, true)]
    [InlineData(IntegrationOperationStatus.Cancelled, true)]
    [InlineData(IntegrationOperationStatus.FailedPermanent, true)]
    public async Task MaintenanceTerminal_UnsuccessfulCheckoutCannotDiscardAnUnresolvedProviderIntent(IntegrationOperationStatus status, bool expected)
    {
        await SeedHealthTerminalIntent(status);
        using var client = new HttpClient(new HealthTerminalHandler());
        var terminal = new MercadoPagoTerminalService(client, new HealthTerminalSettings(), contextFactory: new TestDbContextFactory(_options));
        Assert.Equal(expected, await terminal.RequiresReconciliationAsync("HEALTH-CARD"));
        Assert.False(await terminal.RequiresReconciliationAsync("ABSENT"));
    }

    private async Task SeedHealthTerminalIntent(IntegrationOperationStatus status = IntegrationOperationStatus.Pending)
    {
        await using var db = Db();
        db.PaymentTerminalOperations.Add(new PaymentTerminalOperationEntity
        {
            Id = "HEALTH-INTENT",
            ExternalReference = "HEALTH-CARD",
            IdempotencyKey = "HEALTH-IDEMPOTENCY",
            TerminalId = "SYNTHETIC_TERMINAL",
            Amount = 20m,
            Status = status
        });
        await db.SaveChangesAsync();
    }
    private Task PrepareHealthCheckout() => Sales.BeginCheckoutAsync(Sale("HEALTH-CARD") with
    { PaymentMethod = "Tarjeta", CashAmount = 0m, CardAmount = 20m });
    private sealed class HealthTerminalHandler(Action? sent = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; sent?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"id":"ORD-HEALTH","type":"point","country_code":"MEX","external_reference":"HEALTH-CARD","status":"created","status_detail":"created","last_updated_date":"2026-10-20T12:00:00Z","config":{"point":{"terminal_id":"SYNTHETIC_TERMINAL"}},"transactions":{"payments":[{"id":"PAY-HEALTH","amount":"20.00","status":"created","status_detail":"created"}]}}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class HealthTerminalSettings : ISettingsService
    {
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(key == "TerminalDeviceId" ? "SYNTHETIC_TERMINAL" : null);
        public Task SetAsync(string key, string value) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> GetAllAsync() => throw new NotSupportedException();
        public Task<Ticketfy.Core.Models.Settings.AppSettings> GetAppSettingsAsync() => throw new NotSupportedException();
        public Task SaveAppSettingsAsync(Ticketfy.Core.Models.Settings.AppSettings settings) => throw new NotSupportedException();
    }
}
