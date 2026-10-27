using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Core.Services;
using Ticketfy.Services;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task TerminalEvidence_ConcurrentLookupCannotOverwriteNewerCaptureWithOldPendingResponse()
    {
        await PrepareTerminalCheckout();
        using var initialClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder("created")));
        Assert.False((await BankTerminal(initialClient).ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayedClient = new HttpClient(new AsyncEvidenceHttpHandler(async (_, _) =>
        {
            entered.SetResult(); await release.Task;
            return JsonBank(BankOrder("at_terminal", "2026-10-20T12:00:01Z"));
        }));
        var slow = BankTerminal(delayedClient).RefreshPaymentAsync("BANK-EVIDENCE");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var fastClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder(updated: "2026-10-20T12:00:02Z")));
        try { Assert.True((await BankTerminal(fastClient).RefreshPaymentAsync("BANK-EVIDENCE")).Data!.CanComplete); }
        finally { release.TrySetResult(); }
        Assert.False((await slow).IsSuccess);
        await using var db = Db();
        var saved = await db.PaymentTerminalOperations.SingleAsync();
        Assert.True(saved.FundsObserved); Assert.Equal(IntegrationOperationStatus.Completed, saved.Status);
        Assert.Equal("PAY-EVIDENCE", saved.ProviderPaymentId);
        Assert.Equal(2, await db.TransactionJournal.CountAsync(j => j.EventType == "TERMINAL_ORDER_VERIFIED"));
    }

    [Fact]
    public async Task TerminalEvidence_ConcurrentCreationSendsOnlyOnePostAcrossServiceInstances()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, _) =>
        {
            var factory = new TestDbContextFactory(options);
            var actor = await CommercialTestSession.PrepareCardCheckoutAsync(factory, "BANK-EVIDENCE", 20m);
            int posts = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(new AsyncEvidenceHttpHandler(async (request, _) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method); Interlocked.Increment(ref posts);
                entered.SetResult(); await release.Task;
                return JsonBank(BankOrder());
            }));
            MercadoPagoTerminalService Create() => new(client, new HealthTerminalSettings(), contextFactory: factory,
                accessTokenProvider: () => Task.FromResult<string?>("TEST_TOKEN"), operations: actor, pollingInterval: TimeSpan.Zero);
            var first = Task.Run(() => Create().ProcessPaymentAsync(20m, "BANK-EVIDENCE", default));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            try { Assert.False((await Create().ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess); }
            finally { release.TrySetResult(); }
            Assert.True((await first).IsSuccess); Assert.Equal(1, posts);
            await using var db = await factory.CreateDbContextAsync();
            Assert.Single(await db.PaymentTerminalOperations.ToListAsync());
            Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "TERMINAL_ORDER_VERIFIED").ToListAsync());
        });
    }

    [Fact]
    public async Task TerminalEvidence_ChangedApproverCannotReceiveAttributionForAnOldQuery()
    {
        await PrepareTerminalCheckout();
        using var initialClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder("created")));
        await BankTerminal(initialClient).ProcessPaymentAsync(20m, "BANK-EVIDENCE", default);
        var session = new SessionManager(); var engine = new UserRolePermissionEngine();
        await using (var db = Db()) session.StartSession(engine.MapToModel(await db.Users.SingleAsync()));
        var actor = new CommercialOperationContext(session, engine, "TEST-REGISTER");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncEvidenceHttpHandler(async (_, _) =>
        { entered.SetResult(); await release.Task; return JsonBank(BankOrder(updated: "2026-10-20T12:00:01Z")); }));
        var service = new MercadoPagoTerminalService(client, new HealthTerminalSettings(), contextFactory: new TestDbContextFactory(_options),
            accessTokenProvider: () => Task.FromResult<string?>("TEST_TOKEN"), operations: actor);
        var query = service.RefreshPaymentAsync("BANK-EVIDENCE");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await using (var db = Db())
        {
            var replacementId = Guid.NewGuid();
            CommercialTestSession.Create(db, false, userId: replacementId);
            session.StartSession(engine.MapToModel(await db.Users.SingleAsync(u => u.Id == replacementId)));
        }
        release.SetResult(); Assert.False((await query).IsSuccess);
        await using var verify = Db();
        Assert.Equal(IntegrationOperationStatus.Processing, (await verify.PaymentTerminalOperations.SingleAsync()).Status);
        Assert.Single(await verify.TransactionJournal.Where(j => j.EventType == "TERMINAL_ORDER_VERIFIED").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEvidence_SessionChangeBeforeBankRequestCannotSendAnOldAuthorization(bool cancel)
    {
        await PrepareTerminalCheckout();
        if (cancel)
        {
            using var initialClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder("created")));
            await BankTerminal(initialClient).ProcessPaymentAsync(20m, "BANK-EVIDENCE", default);
        }
        var session = new SessionManager(); var engine = new UserRolePermissionEngine();
        await using (var db = Db()) session.StartSession(engine.MapToModel(await db.Users.SingleAsync()));
        var actor = new CommercialOperationContext(session, engine, "TEST-REGISTER");
        int calls = 0;
        using var client = new HttpClient(new EvidenceHttpHandler(_ => { calls++; return BankOrder(); }));
        var service = new MercadoPagoTerminalService(client, new HealthTerminalSettings(), contextFactory: new TestDbContextFactory(_options),
            accessTokenProvider: async () =>
            {
                await using var db = Db();
                var replacementId = Guid.NewGuid();
                CommercialTestSession.Create(db, false, userId: replacementId);
                session.StartSession(engine.MapToModel(await db.Users.SingleAsync(u => u.Id == replacementId)));
                return "TEST_TOKEN";
            }, operations: actor);
        if (cancel) Assert.False((await service.CancelPaymentIntentAsync("BANK-EVIDENCE")).IsSuccess);
        else Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.Equal(0, calls);
        await using var verify = Db();
        Assert.Empty(await verify.Sales.ToListAsync());
        Assert.Equal(cancel ? 1 : 0, await verify.PaymentTerminalOperations.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEvidence_CancellationRequiresAQueriedFinalUnfundedState(bool final)
    {
        await PrepareTerminalCheckout();
        using var initialClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder("created")));
        await BankTerminal(initialClient).ProcessPaymentAsync(20m, "BANK-EVIDENCE", default);
        int posts = 0, gets = 0;
        using var client = new HttpClient(new AsyncEvidenceHttpHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++; Assert.EndsWith("/ORD-EVIDENCE/cancel", request.RequestUri!.AbsolutePath);
                Assert.EndsWith("-cancel", request.Headers.GetValues("X-Idempotency-Key").Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }
            gets++;
            return Task.FromResult(JsonBank(BankOrder(final ? "canceled" : "at_terminal", "2026-10-20T12:00:01Z")));
        }));
        var service = BankTerminal(client);
        Assert.Equal(final, (await service.CancelPaymentIntentAsync("BANK-EVIDENCE")).IsSuccess);
        Assert.Equal(!final, await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
        Assert.Equal(1, posts); Assert.Equal(1, gets);
        if (final)
        {
            Assert.False((await service.CancelPaymentIntentAsync("BANK-EVIDENCE")).IsSuccess);
            Assert.Equal(1, posts);
        }
    }

    [Fact]
    public async Task TerminalEvidence_CaptureDuringCredentialReadPreventsCancellation()
    {
        await PrepareTerminalCheckout();
        using var initialClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder("created")));
        await BankTerminal(initialClient).ProcessPaymentAsync(20m, "BANK-EVIDENCE", default);
        int calls = 0;
        using var client = new HttpClient(new EvidenceHttpHandler(_ => { calls++; return BankOrder(); }));
        var service = new MercadoPagoTerminalService(client, new HealthTerminalSettings(), contextFactory: new TestDbContextFactory(_options),
            accessTokenProvider: async () =>
            {
                using var captureClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder(updated: "2026-10-20T12:00:01Z")));
                Assert.True((await BankTerminal(captureClient).RefreshPaymentAsync("BANK-EVIDENCE")).Data!.CanComplete);
                return "TEST_TOKEN";
            }, operations: _actor);
        Assert.False((await service.CancelPaymentIntentAsync("BANK-EVIDENCE")).IsSuccess);
        Assert.Equal(0, calls);
        await using var db = Db();
        Assert.Equal(IntegrationOperationStatus.Completed, (await db.PaymentTerminalOperations.SingleAsync()).Status);
        Assert.Equal(2, await db.TransactionJournal.CountAsync(j => j.EventType == "TERMINAL_ORDER_VERIFIED"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEvidence_UnreadableOrOversizedResponseRetainsOriginalIntent(bool oversized)
    {
        await PrepareTerminalCheckout();
        using var client = new HttpClient(new AsyncEvidenceHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(oversized ? new string('x', 150000) : "{invalid", Encoding.UTF8, "application/json") })));
        var service = BankTerminal(client);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.True(await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
        await using var db = Db();
        Assert.Equal(IntegrationOperationStatus.NeedsReconciliation, (await db.PaymentTerminalOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task TerminalEvidence_ObservationAndJournalRollBackTogetherOnStorageFailure()
    {
        await PrepareTerminalCheckout();
        using var initialClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder("created")));
        await BankTerminal(initialClient).ProcessPaymentAsync(20m, "BANK-EVIDENCE", default);
        await using (var db = Db())
            await db.Database.ExecuteSqlRawAsync("""CREATE TRIGGER fail_bank_journal BEFORE INSERT ON transaction_journal WHEN NEW.event_type = 'TERMINAL_ORDER_VERIFIED' BEGIN SELECT RAISE(ABORT, 'synthetic journal failure'); END;""");
        using var client = new HttpClient(new EvidenceHttpHandler(_ => BankOrder(updated: "2026-10-20T12:00:01Z")));
        Assert.False((await BankTerminal(client).RefreshPaymentAsync("BANK-EVIDENCE")).IsSuccess);
        await using var verify = Db();
        var saved = await verify.PaymentTerminalOperations.SingleAsync();
        Assert.Equal(IntegrationOperationStatus.Processing, saved.Status); Assert.False(saved.FundsObserved);
        Assert.Single(await verify.TransactionJournal.Where(j => j.EventType == "TERMINAL_ORDER_VERIFIED").ToListAsync());
        Assert.Empty(await verify.Sales.ToListAsync());
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(422, true)]
    [InlineData(408, false)]
    [InlineData(409, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    public async Task TerminalEvidence_OnlyExplicitCreationRejectionProvesNoFundsAndNeverReposts(int code, bool noFunds)
    {
        await PrepareTerminalCheckout();
        int calls = 0;
        using var client = new HttpClient(new AsyncEvidenceHttpHandler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)); }));
        var service = BankTerminal(client);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.Equal(!noFunds, await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.Equal(1, calls);
        await using var db = Db();
        var intent = await db.PaymentTerminalOperations.SingleAsync();
        Assert.Equal(noFunds, intent.NoFundsConfirmed); Assert.False(intent.FundsObserved);
        Assert.Empty(await db.Sales.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEvidence_UnavailableReadDoesNotEraseCaptureOrCreateAnotherOrder(bool unauthorized)
    {
        await PrepareTerminalCheckout();
        using var initialClient = new HttpClient(new EvidenceHttpHandler(_ => BankOrder()));
        Assert.True((await BankTerminal(initialClient).ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        using var client = new HttpClient(new AsyncEvidenceHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(unauthorized ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable));
        }));
        Assert.False((await BankTerminal(client).RefreshPaymentAsync("BANK-EVIDENCE")).IsSuccess);
        await using var db = Db();
        var intent = await db.PaymentTerminalOperations.SingleAsync();
        Assert.True(intent.FundsObserved); Assert.False(intent.NoFundsConfirmed);
        Assert.Equal(IntegrationOperationStatus.Completed, intent.Status);
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "TERMINAL_ORDER_VERIFIED").ToListAsync());
    }

    private static HttpResponseMessage JsonBank(JsonObject data) => new(HttpStatusCode.OK)
    { Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json") };
    private sealed class AsyncEvidenceHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
