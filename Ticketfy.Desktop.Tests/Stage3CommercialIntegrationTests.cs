using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Hardware;
using Ticketfy.Core.Models;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels.Dialogs.Checkout;

namespace Ticketfy.Desktop.Tests;

public sealed class Stage3CommercialIntegrationTests
{
    [Fact]
    public async Task SaleAndFiscalOutbox_AreCommittedAtomicallyAndOnlyOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Products.Add(new ProductEntity
            {
                Id = "P1",
                Barcode = "7500000000001",
                Name = "Producto",
                Cost = 5m,
                Price = 10m,
                Stock = 2,
                Category = "General",
                Unit = "Pza"
            });
            await setup.SaveChangesAsync();
        }
        var service = new SaleService(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService(), CommercialTestSession.Create(options));
        var sale = new SaleDto(
            "", DateTimeOffset.UtcNow.ToString("O"),
            [new SaleItemSnapshotDto("P1", "Producto", 10m, 5m, 1, "Pza", "General", 0m, 10m)],
            10m, 5m, 5m, 10m, 0m, "Efectivo", null, false, false, null,
            CashAmount: 10m, OperationId: "sale-with-fiscal-outbox");
        var request = ValidCfdi();
        var outbox = new FiscalOutboxRequest(request, "cfdi-issue-sale-with-fiscal-outbox");

        SaleDto first = await service.SaveAsync(sale, outbox);
        SaleDto repeated = await service.SaveAsync(sale, outbox);

        await using var verify = new AppDbContext(options);
        Assert.Equal(first.Id, repeated.Id);
        Assert.Single(await verify.Sales.ToListAsync());
        var queued = Assert.Single(await verify.FiscalOperations.ToListAsync());
        Assert.Equal(first.Id, queued.SaleId);
        Assert.Equal(IntegrationOperationStatus.Pending, queued.Status);
        Assert.Equal(1d, (await verify.Products.SingleAsync()).Stock);
    }

    [Fact]
    public async Task FiscalQueue_RepeatedEnqueueAndProcessing_ProducesOneCfdiAndVerifiedFiles()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var provider = new FakeFacturamaService();
        string documents = Path.Combine(Path.GetTempPath(), $"ticketfy-fiscal-{Guid.NewGuid():N}");
        try
        {
            var queue = new FiscalIntegrationQueueService(fixture.Factory, provider, documents);
            var first = await queue.EnqueueIssueAsync("SALE-1", ValidCfdi(), "issue-once");
            var repeated = await queue.EnqueueIssueAsync("SALE-1", ValidCfdi(), "issue-once");
            Assert.Equal(first.Id, repeated.Id);

            Assert.True(await queue.ProcessNextAsync());
            Assert.False(await queue.ProcessNextAsync());

            await using var verify = fixture.CreateContext();
            var operation = await verify.FiscalOperations.SingleAsync();
            Assert.Equal(IntegrationOperationStatus.Completed, operation.Status);
            Assert.Equal(1, provider.CreateCalls);
            Assert.True(File.Exists(operation.XmlPath));
            Assert.True(File.Exists(operation.PdfPath));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("<xml/>"))), operation.XmlSha256);
        }
        finally
        {
            if (Directory.Exists(documents)) Directory.Delete(documents, recursive: true);
        }
    }

    [Fact]
    public async Task FiscalQueue_AmbiguousDelivery_IsQuarantinedInsteadOfRetried()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var provider = new FakeFacturamaService(throwAmbiguous: true);
        var queue = new FiscalIntegrationQueueService(fixture.Factory, provider, Path.GetTempPath());
        await queue.EnqueueIssueAsync("SALE-2", ValidCfdi(), "ambiguous-issue");

        Assert.True(await queue.ProcessNextAsync());
        Assert.False(await queue.ProcessNextAsync());

        await using var verify = fixture.CreateContext();
        Assert.Equal(IntegrationOperationStatus.NeedsReconciliation, (await verify.FiscalOperations.SingleAsync()).Status);
        Assert.Equal(1, provider.CreateCalls);
    }

    [Fact]
    public async Task FiscalCancellation_IsIdempotentAndRequiresReplacementForMotive01()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var provider = new FakeFacturamaService();
        var queue = new FiscalIntegrationQueueService(fixture.Factory, provider, Path.GetTempPath());
        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueCancellationAsync(
            "SALE-3", "CFDI123", "01", null, "cancel-invalid"));

        var first = await queue.EnqueueCancellationAsync("SALE-3", "CFDI123", "02", null, "cancel-once");
        var repeated = await queue.EnqueueCancellationAsync("SALE-3", "CFDI123", "02", null, "cancel-once");
        Assert.Equal(first.Id, repeated.Id);
        Assert.True(await queue.ProcessNextAsync());
        Assert.False(await queue.ProcessNextAsync());
        Assert.Equal(1, provider.CancelCalls);
    }

    [Fact]
    public async Task MercadoPagoOrders_UsesProviderOrderIdAndStableIdempotencyKey()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var handler = new MercadoPagoHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadopago.com") };
        var settings = new MemorySettings();
        await settings.SetAsync("TerminalDeviceId", "NEWLAND_N950__TEST123");
        var actor = await CommercialTestSession.PrepareCardCheckoutAsync(fixture.Factory, "sale_ref_123", 24m);
        var service = new MercadoPagoTerminalService(
            client, settings, contextFactory: fixture.Factory,
            accessTokenProvider: () => Task.FromResult<string?>("TEST_TOKEN"),
            pollingInterval: TimeSpan.Zero, maxPollingAttempts: 2, operations: actor);

        var result = await service.ProcessPaymentAsync(24m, "sale_ref_123", CancellationToken.None);
        var repeated = await service.ProcessPaymentAsync(24m, "sale_ref_123", CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(repeated.IsSuccess, repeated.ErrorMessage);
        Assert.Equal("PAY123", result.Data!.TransactionId);
        Assert.Equal(1, handler.PostCalls);
        Assert.Equal("/v1/orders/ORD123", handler.LastGetPath);
        Assert.False(string.IsNullOrWhiteSpace(handler.IdempotencyKey));
        Assert.Equal("sale_ref_123", handler.ExternalReference);
        await using var verify = fixture.CreateContext();
        var operation = await verify.PaymentTerminalOperations.SingleAsync();
        Assert.Equal("ORD123", operation.ProviderOrderId);
        Assert.Equal(IntegrationOperationStatus.Completed, operation.Status);
    }

    [Fact]
    public async Task CheckoutRequiringReconciliation_IsNeverConvertedIntoSaleOnRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Products.Add(new ProductEntity { Id = "P2", Barcode = "7500000000002", Name = "Producto", Cost = 5m, Price = 10m, Stock = 2, Category = "General", Unit = "Pza" });
            await setup.SaveChangesAsync();
        }
        var service = new SaleService(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService(), CommercialTestSession.Create(options));
        var sale = new SaleDto("", DateTimeOffset.UtcNow.ToString("O"),
            [new SaleItemSnapshotDto("P2", "Producto", 10m, 5m, 1, "Pza", "General", 0m, 10m)],
            10m, 5m, 5m, 10m, 0m, "Tarjeta", null, false, false, null,
            CardAmount: 10m, OperationId: "uncertain-payment");
        await service.BeginCheckoutAsync(sale);
        await service.MarkCheckoutFailedAsync("uncertain-payment", "Proveedor sin respuesta", requiresReconciliation: true);

        Assert.Equal(0, await service.RecoverInterruptedCheckoutsAsync());
        await using var verify = new AppDbContext(options);
        Assert.Empty(await verify.Sales.ToListAsync());
        Assert.Equal(2d, (await verify.Products.SingleAsync()).Stock);
    }

    [Fact]
    public void WebhookSignature_IsTimeBoundAndComparedWithHmac()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        string manifest = "id:ord123;request-id:req-1;ts:1800000000;";
        string signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();

        Assert.True(MercadoPagoWebhookVerifier.IsValid(
            $"ts=1800000000,v1={signature}", "req-1", "ORD123", "secret", now));
        Assert.False(MercadoPagoWebhookVerifier.IsValid(
            $"ts=1800000000,v1={signature}", "req-2", "ORD123", "secret", now));
        Assert.False(MercadoPagoWebhookVerifier.IsValid(
            $"ts=1800000000,v1={signature}", "req-1", "ORD123", "secret", now.AddHours(1)));

        string millisecondManifest = "id:ord123;request-id:req-1;ts:1800000000000;";
        string millisecondSignature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes(millisecondManifest))).ToLowerInvariant();
        Assert.True(MercadoPagoWebhookVerifier.IsValid(
            $"ts=1800000000000,v1={millisecondSignature}", "req-1", "ORD123", "secret", now));
    }

    [Fact]
    public async Task WebhookInbox_DeduplicatesProviderEventWithoutPersistingPayload()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var inbox = new IntegrationWebhookInbox(fixture.Factory);
        string payload = "{\"data\":{\"id\":\"ORD123\"}}";
        Assert.True(await inbox.TryAcceptAsync("mercadopago", "evt-1", "ORD123", payload));
        Assert.False(await inbox.TryAcceptAsync("mercadopago", "evt-1", "ORD123", payload));
        await using var verify = fixture.CreateContext();
        var stored = await verify.IntegrationWebhookEvents.SingleAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), stored.PayloadSha256);
        Assert.DoesNotContain("data", stored.PayloadSha256, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ST,GS,+ 1.250 kg", "1.250")]
    [InlineData("STABLE: 750 g", "0.750")]
    public void ScaleParser_AcceptsOnlyStableKnownFrames(string frame, string expected)
    {
        Assert.True(ScaleReadingParser.TryParseStableKilograms(frame, out decimal value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
        Assert.False(ScaleReadingParser.TryParseStableKilograms("US,GS,1.250 kg", out _));
    }

    [Fact]
    public void BarcodeBuffer_EmitsOneCodeAtTerminator()
    {
        var buffer = new BarcodeInputBuffer();
        Assert.Null(buffer.Push('7'));
        Assert.Null(buffer.Push('5'));
        Assert.Equal("75", buffer.Push('\r'));
        Assert.Null(buffer.Push('\n'));
    }

    [Theory]
    [InlineData("ImpresoraTickets")]
    [InlineData("COM3@19200")]
    [InlineData("tcp://192.168.1.50:9100")]
    public void PrinterTargetValidator_AcceptsSupportedTransports(string target)
    {
        Assert.True(HardwareCompatibility.TryValidatePrinterTarget(target, out string error), error);
        Assert.False(HardwareCompatibility.TryValidatePrinterTarget("COM0@999999", out _));
        Assert.False(HardwareCompatibility.TryValidatePrinterTarget("C:\\temp\\printer", out _));
    }

    [Fact]
    public void CfdiBuilder_ProducesCompleteTaxedConceptWithoutDoubleMultiplyingTax()
    {
        var (request, error) = CheckoutInvoiceHandler.BuildInvoiceRequest(
            "XAXX010101000", "PUBLICO EN GENERAL", "S01 - Sin efectos fiscales",
            "616 - Sin obligaciones fiscales", "06000",
            [new SaleItemSnapshotDto("P1", "Producto", 10m, 5m, 3, "Pza", "General", 0m, 30m)]);

        Assert.Null(error);
        var item = Assert.Single(request!.Items);
        Assert.Equal("01", request.Exportation);
        Assert.Equal("02", item.TaxObject);
        Assert.Equal(25.86m, item.Subtotal);
        Assert.Equal(4.14m, Assert.Single(item.Taxes).Total);
        Assert.Equal(30m, item.Total);
    }

    private static FacturamaCfdiRequest ValidCfdi() => new()
    {
        ExpeditionPlace = "06000",
        Receiver = new CfdiReceiver { Rfc = "XAXX010101000", Name = "PUBLICO EN GENERAL", TaxZipCode = "06000" },
        Items = [new CfdiItem { ProductCode = "01010101", UnitCode = "H87", Description = "Producto", Quantity = 1m, UnitPrice = 10m, Subtotal = 10m }]
    };

    private sealed class FakeFacturamaService(bool throwAmbiguous = false) : IFacturamaService
    {
        public int CreateCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public Task<FacturamaCfdiResponse?> CreateInvoiceAsync(FacturamaCfdiRequest request, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            if (throwAmbiguous) throw new FacturamaProviderException("uncertain", isTransient: true, deliveryUncertain: true);
            return Task.FromResult<FacturamaCfdiResponse?>(new FacturamaCfdiResponse { Id = "CFDI123", Status = "active", Uuid = "UUID123", IsActive = true });
        }
        public Task<FacturamaCfdiResponse?> GetInvoiceAsync(string providerDocumentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FacturamaCfdiResponse?>(new FacturamaCfdiResponse { Id = providerDocumentId, Status = "active", Uuid = "UUID123", IsActive = true });
        public Task<FacturamaFileResponse> DownloadInvoiceAsync(string providerDocumentId, FacturamaFileFormat format, CancellationToken cancellationToken = default)
        {
            byte[] content = Encoding.UTF8.GetBytes(format == FacturamaFileFormat.Xml ? "<xml/>" : "%PDF-test");
            return Task.FromResult(new FacturamaFileResponse { ContentEncoding = "base64", ContentLength = content.Length, Content = Convert.ToBase64String(content) });
        }
        public Task<FacturamaCancellationResponse> CancelInvoiceAsync(string providerDocumentId, string motive, string? replacementUuid = null, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            return Task.FromResult(new FacturamaCancellationResponse { Status = "canceled", Uuid = "UUID123" });
        }
    }

    private sealed class MercadoPagoHandler : HttpMessageHandler
    {
        public int PostCalls { get; private set; }
        public string? LastGetPath { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? ExternalReference { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/orders")
            {
                PostCalls++;
                IdempotencyKey = request.Headers.GetValues("X-Idempotency-Key").Single();
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
                ExternalReference = payload.RootElement.GetProperty("external_reference").GetString();
                return Json(HttpStatusCode.Created, """
                    {"id":"ORD123","type":"point","country_code":"MEX","external_reference":"sale_ref_123","status":"created","status_detail":"created","last_updated_date":"2026-10-20T12:00:00Z","config":{"point":{"terminal_id":"NEWLAND_N950__TEST123"}},"transactions":{"payments":[{"id":"PAY123","amount":"24.00","status":"created","status_detail":"created"}]}}
                    """);
            }
            LastGetPath = request.RequestUri!.AbsolutePath;
            return Json(HttpStatusCode.OK, """
                {"id":"ORD123","type":"point","country_code":"MEX","external_reference":"sale_ref_123","status":"processed","status_detail":"processed","last_updated_date":"2026-10-20T12:00:01Z","config":{"point":{"terminal_id":"NEWLAND_N950__TEST123"}},"transactions":{"payments":[{"id":"PAY123","amount":"24.00","paid_amount":"24.00","status":"processed","status_detail":"accredited","authorization_code":"AUTH1","payment_method":{"id":"visa","card":{"last_four_digits":"4242"}}}]}}
                """);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);
        public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
        public Task<Dictionary<string, string>> GetAllAsync() => Task.FromResult(new Dictionary<string, string>(_values));
        public Task<Ticketfy.Core.Models.Settings.AppSettings> GetAppSettingsAsync() => Task.FromResult(new Ticketfy.Core.Models.Settings.AppSettings());
        public Task SaveAppSettingsAsync(Ticketfy.Core.Models.Settings.AppSettings settings) => Task.CompletedTask;
    }

    private sealed class IntegrationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory Factory { get; }

        private IntegrationFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _options = options;
            Factory = new TestDbContextFactory(options);
        }

        public static async Task<IntegrationFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            await using var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new IntegrationFixture(connection, options);
        }

        public AppDbContext CreateContext() => new(_options);
        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
