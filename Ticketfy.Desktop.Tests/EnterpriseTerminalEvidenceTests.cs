using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using System.Globalization;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Core.Services;
using Ticketfy.Services;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData(IntegrationOperationStatus.FailedPermanent, "future_status")]
    [InlineData(IntegrationOperationStatus.FailedPermanent, "action_required")]
    [InlineData(IntegrationOperationStatus.Cancelled, "canceled")]
    public async Task TerminalEvidence_LegacyNoFundsLabelIsNotProofAndMustRemainVisible(IntegrationOperationStatus state, string providerStatus)
    {
        await PrepareTerminalCheckout();
        await using (var db = Db())
        {
            (await db.CheckoutOperations.SingleAsync()).State = CheckoutOperationState.Failed;
            db.PaymentTerminalOperations.Add(new PaymentTerminalOperationEntity
            {
                Id = "OLD-UNVERIFIED",
                ExternalReference = "BANK-EVIDENCE",
                IdempotencyKey = "OLD-KEY",
                TerminalId = "SYNTHETIC_TERMINAL",
                Amount = 20m,
                Status = state,
                ProviderStatus = providerStatus
            });
            await db.SaveChangesAsync();
        }
        using var client = new HttpClient(new EvidenceHttpHandler(_ => BankOrder("canceled")));
        var service = BankTerminal(client);
        Assert.True(await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
        Assert.Single(await Sales.GetInterruptedCheckoutsAsync());
        await using (var db = Db()) Assert.True(await ShiftLedgerValidator.HasPendingCheckoutsAsync(db, "SHIFT-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", false, true, "No suponer rechazo antiguo"));
        var proof = await service.RefreshPaymentAsync("BANK-EVIDENCE", "ORD-EVIDENCE");
        Assert.True(proof.Data!.NoFundsConfirmed);
        await Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", false, true, "Cancelación bancaria consultada");
        Assert.Empty(await Sales.GetInterruptedCheckoutsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEvidence_LegacyCompletedLabelCannotAuthorizeNewMoneyBeforeProviderValidation(bool recovery)
    {
        await PrepareTerminalCheckout();
        await using (var db = Db())
        {
            db.PaymentTerminalOperations.Add(new PaymentTerminalOperationEntity
            {
                Id = "LEGACY-BANK",
                ExternalReference = "BANK-EVIDENCE",
                IdempotencyKey = "LEGACY-KEY",
                Amount = 20m,
                TerminalId = "SYNTHETIC_TERMINAL",
                ProviderPaymentId = "PAY-EVIDENCE",
                Status = IntegrationOperationStatus.Completed
            });
            await db.SaveChangesAsync();
        }
        if (recovery) Assert.Equal(0, await Sales.RecoverInterruptedCheckoutsAsync());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.SaveAsync(Sale("BANK-EVIDENCE") with
        { PaymentMethod = "Tarjeta", CashAmount = 0m, CardAmount = 20m }));
        await using (var db = Db())
        {
            Assert.Empty(await db.Sales.ToListAsync());
            Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
        }
        var handler = new EvidenceHttpHandler(_ => BankOrder());
        using var client = new HttpClient(handler);
        Assert.True((await BankTerminal(client).RefreshPaymentAsync("BANK-EVIDENCE", "ORD-EVIDENCE")).Data!.CanComplete);
        await Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", true, true, "Orden original validada");
        Assert.Equal([HttpMethod.Get], handler.Methods);
        await using var verify = Db();
        Assert.Single(await verify.Sales.ToListAsync());
        Assert.True((await verify.PaymentTerminalOperations.SingleAsync()).FundsObserved);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("amount")]
    [InlineData("payment")]
    [InlineData("terminal")]
    [InlineData("type")]
    [InlineData("currency")]
    public async Task TerminalEvidence_MismatchedProviderEvidenceCannotAuthorizeCheckout(string mismatch)
    {
        await PrepareTerminalCheckout();
        var response = BankOrder();
        switch (mismatch)
        {
            case "reference": response["external_reference"] = "ANOTHER-SALE"; break;
            case "amount": response["transactions"]!["payments"]![0]!["amount"] = "0.01"; break;
            case "payment": response["transactions"]!["payments"]![0]!["status"] = "failed"; break;
            case "terminal": response["config"]!["point"]!["terminal_id"] = "ANOTHER-TERMINAL"; break;
            case "type": response["type"] = "online"; break;
            case "currency": response["currency"] = "ARS"; break;
        }
        using var client = new HttpClient(new EvidenceHttpHandler(_ => response));
        var service = BankTerminal(client);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.True(await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
        await using var db = Db();
        Assert.NotEqual(IntegrationOperationStatus.Completed, (await db.PaymentTerminalOperations.SingleAsync()).Status);
        Assert.Empty(await db.Sales.ToListAsync());
    }

    [Fact]
    public async Task TerminalEvidence_UnknownStatusCannotReleaseAnUncertainIntent()
    {
        await PrepareTerminalCheckout();
        using var client = new HttpClient(new EvidenceHttpHandler(_ => BankOrder("future_status")));
        var service = BankTerminal(client);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.True(await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
    }

    [Fact]
    public async Task TerminalEvidence_LostCreationResponseCannotSendAnotherPost()
    {
        await PrepareTerminalCheckout();
        int calls = 0;
        using var client = new HttpClient(new EvidenceHttpHandler(_ =>
        {
            if (++calls == 1) throw new HttpRequestException("Synthetic response lost after acceptance");
            return BankOrder();
        }));
        var service = BankTerminal(client);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.Equal(1, calls);
        Assert.True(await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("expired")]
    [InlineData("failed")]
    public async Task TerminalEvidence_ExplicitUnfundedStatesCanBeResolvedWithoutACharge(string state)
    {
        await PrepareTerminalCheckout();
        var handler = new EvidenceHttpHandler(_ => BankOrder(state));
        using var client = new HttpClient(handler);
        var service = BankTerminal(client);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.False(await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
        var query = await service.RefreshPaymentAsync("BANK-EVIDENCE");
        Assert.True(query.IsSuccess, query.ErrorMessage); Assert.True(query.Data!.NoFundsConfirmed);
        Assert.False(query.Data.CanComplete);
        await Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", false, true, "Rechazo bancario comprobado");
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.Equal([HttpMethod.Post, HttpMethod.Get], handler.Methods);
        await using var db = Db(); Assert.Empty(await db.Sales.ToListAsync());
    }

    [Theory]
    [InlineData("action_required")]
    [InlineData("refunded")]
    [InlineData("partially_refunded")]
    [InlineData("in_review")]
    public async Task TerminalEvidence_PendingRefundedOrReviewStatesCannotAuthorizeOrDiscard(string state)
    {
        await PrepareTerminalCheckout();
        var response = BankOrder(state == "partially_refunded" ? "processed" : state == "in_review" ? "failed" : state);
        var payment = response["transactions"]!["payments"]![0]!;
        if (state == "partially_refunded") { response["status_detail"] = state; payment["refunded_amount"] = "1.00"; }
        if (state == "refunded") { payment["paid_amount"] = "20.00"; payment["refunded_amount"] = "20.00"; }
        if (state == "in_review") payment["status_detail"] = state;
        using var client = new HttpClient(new EvidenceHttpHandler(_ => response));
        var service = BankTerminal(client);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        Assert.True(await service.RequiresReconciliationAsync("BANK-EVIDENCE"));
        var query = await service.RefreshPaymentAsync("BANK-EVIDENCE");
        Assert.True(query.IsSuccess, query.ErrorMessage);
        Assert.False(query.Data!.CanComplete); Assert.False(query.Data.NoFundsConfirmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", false, true, "No inventar ausencia de fondos"));
        await using var db = Db(); Assert.Empty(await db.Sales.ToListAsync());
    }

    [Fact]
    public async Task TerminalEvidence_ReadOnlyLookupBindsLostOrderAndCompletesOriginalSaleOnce()
    {
        await PrepareTerminalCheckout();
        var handler = new EvidenceHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post) throw new HttpRequestException("Lost acknowledgement");
            Assert.Equal("/v1/orders/ORD-EVIDENCE", request.RequestUri!.AbsolutePath);
            return BankOrder();
        });
        using var client = new HttpClient(handler); var service = BankTerminal(client);
        Assert.False((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        await Sales.MarkCheckoutFailedAsync("BANK-EVIDENCE", "Unknown bank", true);
        Assert.False((await service.RefreshPaymentAsync("BANK-EVIDENCE")).IsSuccess);
        var first = await service.RefreshPaymentAsync("BANK-EVIDENCE", "ORD-EVIDENCE");
        var repeat = await service.RefreshPaymentAsync("BANK-EVIDENCE", "ORD-EVIDENCE");
        Assert.True(first.Data!.CanComplete); Assert.True(repeat.Data!.CanComplete);
        await Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", true, true, "Banco consultado");
        await Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", true, true, "Banco consultado");
        Assert.Equal([HttpMethod.Post, HttpMethod.Get, HttpMethod.Get], handler.Methods);
        await using var db = Db();
        var operation = await db.PaymentTerminalOperations.SingleAsync();
        Assert.True(operation.FundsObserved);
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "TERMINAL_ORDER_VERIFIED").ToListAsync());
        Assert.Single(await db.Sales.ToListAsync()); Assert.Single(await db.SalePayments.ToListAsync());
        Assert.Equal(18d, (await db.Products.SingleAsync()).Stock);
    }

    [Fact]
    public async Task TerminalEvidence_StaleAndContradictoryResponsesDoNotEraseCapturedFunds()
    {
        await PrepareTerminalCheckout();
        var response = BankOrder(updated: "2026-10-20T12:00:02Z");
        using var client = new HttpClient(new EvidenceHttpHandler(_ => response));
        var service = BankTerminal(client);
        Assert.True((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        string capturedJournalId, capturedDetails;
        await using (var db = Db())
        {
            var entry = await db.TransactionJournal.SingleAsync(j => j.EventType == "TERMINAL_ORDER_VERIFIED");
            capturedJournalId = entry.Id; capturedDetails = entry.Details;
            Assert.Contains((await db.PaymentTerminalOperations.SingleAsync()).ProviderEvidenceHash!, capturedDetails);
            Assert.Contains("PAY-EVIDENCE", capturedDetails); Assert.Contains("2026-10-20T12:00:02", capturedDetails);
        }
        response = BankOrder("created", "2026-10-20T12:00:01Z");
        Assert.False((await service.RefreshPaymentAsync("BANK-EVIDENCE")).IsSuccess);
        await using (var db = Db()) Assert.Equal(IntegrationOperationStatus.Completed, (await db.PaymentTerminalOperations.SingleAsync()).Status);
        response = BankOrder("failed", "2026-10-20T12:00:02Z");
        var conflict = await service.RefreshPaymentAsync("BANK-EVIDENCE");
        Assert.False(conflict.Data!.CanComplete); Assert.False(conflict.Data.NoFundsConfirmed);
        await using (var db = Db()) Assert.True((await db.PaymentTerminalOperations.SingleAsync()).FundsObserved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.ResolveInterruptedCheckoutAsync("BANK-EVIDENCE", false, true, "No borrar captura"));
        response = BankOrder(updated: "2026-10-20T12:00:03Z");
        Assert.True((await service.RefreshPaymentAsync("BANK-EVIDENCE")).Data!.CanComplete);
        response = BankOrder("refunded", "2026-10-20T12:00:04Z");
        response["transactions"]!["payments"]![0]!["refunded_amount"] = "20.00";
        Assert.False((await service.RefreshPaymentAsync("BANK-EVIDENCE")).Data!.CanComplete);
        response = BankOrder(updated: "2026-10-20T12:00:05Z");
        var cannotUnrefund = await service.RefreshPaymentAsync("BANK-EVIDENCE");
        Assert.False(cannotUnrefund.Data!.CanComplete); Assert.False(cannotUnrefund.Data.NoFundsConfirmed);
        await using var verify = Db();
        Assert.Equal(capturedDetails, (await verify.TransactionJournal.SingleAsync(j => j.Id == capturedJournalId)).Details);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("payment-id")]
    [InlineData("date")]
    [InlineData("paid")]
    [InlineData("refund")]
    [InlineData("multiple-payments")]
    public async Task TerminalEvidence_InvalidLookupPreservesThePreviouslyBoundOrder(string field)
    {
        await PrepareTerminalCheckout();
        var response = BankOrder();
        using var client = new HttpClient(new EvidenceHttpHandler(_ => response)); var service = BankTerminal(client);
        Assert.True((await service.ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        var payment = response["transactions"]!["payments"]![0]!;
        switch (field)
        {
            case "order": response["id"] = "ORD-OTHER"; break;
            case "payment-id": payment["id"] = "PAY-OTHER"; break;
            case "date": response["last_updated_date"] = "ayer"; break;
            case "paid": payment["paid_amount"] = "20.001"; break;
            case "refund": payment["refunded_amount"] = "21"; break;
            case "multiple-payments": response["transactions"]!["payments"]!.AsArray().Add(payment.DeepClone()); break;
        }
        Assert.False((await service.RefreshPaymentAsync("BANK-EVIDENCE")).IsSuccess);
        await using var db = Db();
        var saved = await db.PaymentTerminalOperations.SingleAsync();
        Assert.Equal("ORD-EVIDENCE", saved.ProviderOrderId); Assert.Equal("PAY-EVIDENCE", saved.ProviderPaymentId);
        Assert.Equal(IntegrationOperationStatus.Completed, saved.Status); Assert.True(saved.FundsObserved);
        Assert.Single(await db.TransactionJournal.Where(j => j.EventType == "TERMINAL_ORDER_VERIFIED").ToListAsync());
    }

    [Theory]
    [InlineData("20.001", "BANK-EVIDENCE")]
    [InlineData("0", "BANK-EVIDENCE")]
    [InlineData("10", "BANK-EVIDENCE")]
    [InlineData("20", "../OTHER")]
    [InlineData("20", "ABSENT")]
    public async Task TerminalEvidence_InvalidLocalRequestDoesNotContactProvider(string amount, string reference)
    {
        await PrepareTerminalCheckout();
        var handler = new EvidenceHttpHandler(_ => throw new InvalidOperationException("No bank request allowed"));
        using var client = new HttpClient(handler);
        Assert.False((await BankTerminal(client).ProcessPaymentAsync(decimal.Parse(amount, CultureInfo.InvariantCulture), reference, default)).IsSuccess);
        Assert.Empty(handler.Methods);
        await using var db = Db(); Assert.Empty(await db.PaymentTerminalOperations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEvidence_QueryRequiresBothPermissionsAndOriginalRegister(bool grantRecoveryOnly)
    {
        await PrepareTerminalCheckout();
        var handler = new EvidenceHttpHandler(_ => BankOrder());
        using var client = new HttpClient(handler);
        Assert.True((await BankTerminal(client).ProcessPaymentAsync(20m, "BANK-EVIDENCE", default)).IsSuccess);
        await using var db = Db();
        var other = CommercialTestSession.Create(db, register: "OTHER-BANK", userId: Guid.NewGuid());
        var denied = new MercadoPagoTerminalService(client, new HealthTerminalSettings(), contextFactory: new TestDbContextFactory(_options),
            accessTokenProvider: () => throw new InvalidOperationException("No token for other register"), operations: other);
        Assert.False((await denied.RefreshPaymentAsync("BANK-EVIDENCE")).IsSuccess);
        Assert.False((await denied.CancelPaymentIntentAsync("BANK-EVIDENCE")).IsSuccess);
        var permissions = new SettingsService(new TestDbContextFactory(_options), operations: _actor);
        await permissions.SetAsync("RolePermissions_CAJERO", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, bool>
        { [PermissionCatalog.CheckoutRecover] = grantRecoveryOnly, [PermissionCatalog.PaymentsReconcile] = !grantRecoveryOnly }));
        var cashierId = Guid.NewGuid();
        CommercialTestSession.Create(db, openShift: false, userId: cashierId, role: "CAJERO");
        var engine = new UserRolePermissionEngine(permissions);
        var session = new SessionManager(); session.StartSession(engine.MapToModel(await db.Users.SingleAsync(u => u.Id == cashierId)));
        var cashier = new CommercialOperationContext(session, engine, "TEST-REGISTER");
        var restricted = new MercadoPagoTerminalService(client, new HealthTerminalSettings(), contextFactory: new TestDbContextFactory(_options),
            accessTokenProvider: () => throw new InvalidOperationException("No token for unprivileged user"), operations: cashier);
        Assert.False((await restricted.RefreshPaymentAsync("BANK-EVIDENCE")).IsSuccess);
        Assert.Single(handler.Methods);
    }

    private Task PrepareTerminalCheckout() => Sales.BeginCheckoutAsync(Sale("BANK-EVIDENCE") with
    { PaymentMethod = "Tarjeta", CashAmount = 0m, CardAmount = 20m });

    private MercadoPagoTerminalService BankTerminal(HttpClient client) => new(client, new HealthTerminalSettings(),
        contextFactory: new TestDbContextFactory(_options), accessTokenProvider: () => Task.FromResult<string?>("TEST_TOKEN"),
        pollingInterval: TimeSpan.Zero, maxPollingAttempts: 1, operations: _actor);

    private static JsonObject BankOrder(string status = "processed", string updated = "2026-10-20T12:00:00Z")
    {
        var result = JsonNode.Parse("""
        {"id":"ORD-EVIDENCE","type":"point","country_code":"MEX","external_reference":"BANK-EVIDENCE",
         "config":{"point":{"terminal_id":"SYNTHETIC_TERMINAL"}},
         "transactions":{"payments":[{"id":"PAY-EVIDENCE","amount":"20.00"}]}}
        """)!.AsObject();
        result["status"] = status; result["status_detail"] = status;
        result["last_updated_date"] = updated;
        var payment = result["transactions"]!["payments"]![0]!;
        payment["status"] = status;
        payment["status_detail"] = status == "processed" ? "accredited" : status;
        payment["paid_amount"] = status == "processed" ? "20.00" : "0.00";
        return result;
    }

    private sealed class EvidenceHttpHandler(Func<HttpRequestMessage, JsonObject> respond) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(respond(request).ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
}
