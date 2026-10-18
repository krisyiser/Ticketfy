using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Models;
using Ticketfy.Core.Startup;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Desktop.Tests;

public sealed class Stage4ReliabilityTests
{
    [Fact]
    public async Task Backup_WhenDiskHasNoSpace_FailsBeforeCreatingPartialFile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ticketfy-disk-full-{Guid.NewGuid():N}");
        string database = Path.Combine(root, "source.db");
        string backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(database, [1, 2, 3, 4]);

        try
        {
            var service = new BackupService(database, backups, _ => 0);
            var error = await Assert.ThrowsAsync<IOException>(() => service.CreateBackupAsync("disk-full"));

            Assert.Contains("Espacio insuficiente", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(backups));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LockedDatabase_IsRetriedAndUltimatelySurfaced()
    {
        string database = Path.Combine(Path.GetTempPath(), $"ticketfy-locked-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={database};Default Timeout=1;Pooling=False";
        try
        {
            await using var blocker = new SqliteConnection(connectionString);
            await blocker.OpenAsync();
            await using (var schema = blocker.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE sample (id INTEGER PRIMARY KEY);";
                await schema.ExecuteNonQueryAsync();
            }
            await using var transaction = await blocker.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await using (var lockCommand = blocker.CreateCommand())
            {
                lockCommand.Transaction = (SqliteTransaction)transaction;
                lockCommand.CommandText = "INSERT INTO sample(id) VALUES (1);";
                await lockCommand.ExecuteNonQueryAsync();
            }

            int attempts = 0;
            var error = await Assert.ThrowsAsync<SqliteException>(() => DbResilienceHelper.ExecuteWithRetryAsync(async () =>
            {
                attempts++;
                await using var competing = new SqliteConnection(connectionString);
                await competing.OpenAsync();
                await using (var timeout = competing.CreateCommand())
                {
                    timeout.CommandText = "PRAGMA busy_timeout=50;";
                    await timeout.ExecuteNonQueryAsync();
                }
                await using var command = competing.CreateCommand();
                command.CommandTimeout = 1;
                command.CommandText = "INSERT INTO sample(id) VALUES (2);";
                await command.ExecuteNonQueryAsync();
            }));

            Assert.Equal(5, error.SqliteErrorCode);
            Assert.Equal(4, attempts);
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [Fact]
    public async Task InterruptedFiscalWork_ReleasesLeaseAndReturnsToRetryQueue()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var provider = new CancellingFacturamaService(cancellation);
        var now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
        var queue = new FiscalIntegrationQueueService(fixture.Factory, provider, Path.GetTempPath(), () => now);
        await queue.EnqueueIssueAsync("SALE-SHUTDOWN", ValidCfdi(), "fiscal-shutdown");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.ProcessNextAsync(cancellation.Token));

        await using var verify = fixture.CreateContext();
        var operation = await verify.FiscalOperations.SingleAsync();
        Assert.Equal(IntegrationOperationStatus.WaitingRetry, operation.Status);
        Assert.Equal("cancelled", operation.LastErrorCode);
        Assert.Null(operation.LeaseUntilUtc);
    }

    [Fact]
    public async Task FiscalTransientNetworkFailure_RetriesWithoutCreatingAnotherQueueItem()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var provider = new TransientFacturamaService();
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
        string documents = Path.Combine(Path.GetTempPath(), $"ticketfy-fiscal-retry-{Guid.NewGuid():N}");
        try
        {
            var queue = new FiscalIntegrationQueueService(fixture.Factory, provider, documents, () => now);
            await queue.EnqueueIssueAsync("SALE-RETRY", ValidCfdi(), "fiscal-network-retry");

            Assert.True(await queue.ProcessNextAsync());
            now = now.AddSeconds(6);
            Assert.True(await queue.ProcessNextAsync());

            await using var verify = fixture.CreateContext();
            Assert.Single(await verify.FiscalOperations.ToListAsync());
            Assert.Equal(IntegrationOperationStatus.Completed, (await verify.FiscalOperations.SingleAsync()).Status);
            Assert.Equal(2, provider.CreateCalls);
        }
        finally
        {
            if (Directory.Exists(documents)) Directory.Delete(documents, recursive: true);
        }
    }

    [Fact]
    public async Task PaymentNetworkFailure_ReusesIdempotencyKeyOnUserRetry()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var handler = new TransientPaymentHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.mercadopago.com") };
        var settings = new MemorySettings();
        await settings.SetAsync("TerminalDeviceId", "NEWLAND_N950__TEST123");
        var service = new MercadoPagoTerminalService(
            client, settings, contextFactory: fixture.Factory,
            accessTokenProvider: () => Task.FromResult<string?>("TEST_TOKEN"),
            pollingInterval: TimeSpan.Zero, maxPollingAttempts: 1);

        var failed = await service.ProcessPaymentAsync(48m, "sale_network_retry", CancellationToken.None);
        var recovered = await service.ProcessPaymentAsync(48m, "sale_network_retry", CancellationToken.None);

        Assert.False(failed.IsSuccess);
        Assert.True(recovered.IsSuccess, recovered.ErrorMessage);
        Assert.Equal(2, handler.PostCalls);
        Assert.Single(handler.IdempotencyKeys.Distinct(StringComparer.Ordinal));
        await using var verify = fixture.CreateContext();
        Assert.Single(await verify.PaymentTerminalOperations.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentDoubleClick_CommitsSaleStockAndCashOnce()
    {
        string database = Path.Combine(Path.GetTempPath(), $"ticketfy-double-click-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={database};Default Timeout=10;Pooling=False")
                .Options;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Products.Add(new ProductEntity { Id = "P-CLICK", Barcode = "CLICK", Name = "Producto", Cost = 4m, Price = 10m, Stock = 5, Category = "General", Unit = "Pza" });
                setup.Shifts.Add(new ShiftEntity { Id = "SHIFT-CLICK", RegisterId = "TEST-REGISTER", CashierUserId = CommercialTestSession.UserId.ToString(), StartTime = DateTimeOffset.UtcNow.ToString("O"), OpeningBalance = 100m, ExpectedBalance = 100m, IsOpen = 1 });
                await setup.SaveChangesAsync();
            }

            SaleDto sale = new("", DateTimeOffset.UtcNow.ToString("O"),
                [new SaleItemSnapshotDto("P-CLICK", "Producto", 10m, 4m, 1, "Pza", "General", 0m, 10m)],
                10m, 4m, 6m, 10m, 0m, "Efectivo", null, false, false, null,
                CashAmount: 10m, OperationId: "double-click-operation");
            var services = Enumerable.Range(0, 2)
                .Select(_ => new SaleService(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService(), CommercialTestSession.Create(options)))
                .ToArray();

            SaleDto[] results = await Task.WhenAll(services.Select(service => service.SaveAsync(sale)));

            Assert.Equal(results[0].Id, results[1].Id);
            await using var verify = new AppDbContext(options);
            Assert.Equal(1, await verify.Sales.CountAsync());
            Assert.Equal(4d, (await verify.Products.SingleAsync()).Stock);
            Assert.Equal(110m, (await verify.Shifts.SingleAsync()).ExpectedBalance);
            Assert.Equal(1, await verify.TransactionJournal.CountAsync());
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [Fact]
    public async Task InvalidLegacySchema_IsRejectedBeforeMigrationHistoryIsAdopted()
    {
        string database = Path.Combine(Path.GetTempPath(), $"ticketfy-invalid-legacy-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={database};Pooling=False";
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE products (id TEXT PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DatabaseInitializer.AdoptValidatedLegacyHistoryAsync(connectionString));
            Assert.Contains("columnas faltantes", error.Message, StringComparison.OrdinalIgnoreCase);

            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync();
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
            Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
        }
    }

    private static FacturamaCfdiRequest ValidCfdi() => new()
    {
        ExpeditionPlace = "06000",
        Receiver = new CfdiReceiver { Rfc = "XAXX010101000", Name = "PUBLICO EN GENERAL", TaxZipCode = "06000" },
        Items = [new CfdiItem { ProductCode = "01010101", UnitCode = "H87", Description = "Producto", Quantity = 1m, UnitPrice = 10m, Subtotal = 10m }]
    };

    private sealed class CancellingFacturamaService(CancellationTokenSource cancellation) : IFacturamaService
    {
        public Task<FacturamaCfdiResponse?> CreateInvoiceAsync(FacturamaCfdiRequest request, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromCanceled<FacturamaCfdiResponse?>(cancellationToken);
        }
        public Task<FacturamaCfdiResponse?> GetInvoiceAsync(string providerDocumentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FacturamaFileResponse> DownloadInvoiceAsync(string providerDocumentId, FacturamaFileFormat format, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FacturamaCancellationResponse> CancelInvoiceAsync(string providerDocumentId, string motive, string? replacementUuid = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TransientFacturamaService : IFacturamaService
    {
        public int CreateCalls { get; private set; }
        public Task<FacturamaCfdiResponse?> CreateInvoiceAsync(FacturamaCfdiRequest request, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            if (CreateCalls == 1) throw new FacturamaProviderException("network", isTransient: true, deliveryUncertain: false);
            return Task.FromResult<FacturamaCfdiResponse?>(new FacturamaCfdiResponse { Id = "CFDI-RETRY", Status = "active", Uuid = "UUID-RETRY", IsActive = true });
        }
        public Task<FacturamaCfdiResponse?> GetInvoiceAsync(string providerDocumentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FacturamaCfdiResponse?>(new FacturamaCfdiResponse { Id = providerDocumentId, Status = "active", Uuid = "UUID-RETRY", IsActive = true });
        public Task<FacturamaFileResponse> DownloadInvoiceAsync(string providerDocumentId, FacturamaFileFormat format, CancellationToken cancellationToken = default)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(format == FacturamaFileFormat.Xml ? "<xml/>" : "%PDF-test");
            return Task.FromResult(new FacturamaFileResponse { ContentEncoding = "base64", ContentLength = bytes.Length, Content = Convert.ToBase64String(bytes) });
        }
        public Task<FacturamaCancellationResponse> CancelInvoiceAsync(string providerDocumentId, string motive, string? replacementUuid = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TransientPaymentHandler : HttpMessageHandler
    {
        public int PostCalls { get; private set; }
        public List<string> IdempotencyKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/orders")
            {
                PostCalls++;
                IdempotencyKeys.Add(request.Headers.GetValues("X-Idempotency-Key").Single());
                if (PostCalls == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(Json(HttpStatusCode.Created, """
                    {"id":"ORD-RETRY","external_reference":"sale_network_retry","status":"processed","transactions":{"payments":[{"id":"PAY-RETRY","amount":"48.00","status":"processed"}]}}
                    """));
            }
            return Task.FromResult(Json(HttpStatusCode.OK, """
                {"id":"ORD-RETRY","external_reference":"sale_network_retry","status":"processed","transactions":{"payments":[{"id":"PAY-RETRY","amount":"48.00","status":"processed"}]}}
                """));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = [];
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
