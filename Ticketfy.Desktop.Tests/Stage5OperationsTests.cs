using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Ticketfy.Core.Operations;
using Ticketfy.Logging;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Desktop.Tests;

public sealed class Stage5OperationsTests
{
    [Fact]
    public async Task BackupLifecycle_CreatesValidBackupAndProvesTemporaryRestore()
    {
        using var fixture = new OperationsFixture();
        fixture.CreateCommercialDatabase(fixture.ActiveDatabase, "ACTIVE");
        BackupService service = fixture.CreateBackupService();

        string backup = await service.CreateBackupAsync("manual/test");
        BackupValidationResult validation = await service.ValidateBackupAsync(backup);
        BackupValidationResult restoreTest = await service.TestRestoreAsync(backup);

        Assert.True(validation.IsValid, validation.Message);
        Assert.NotNull(validation.Sha256);
        Assert.True(validation.SizeBytes > 0);
        Assert.True(restoreTest.IsValid, restoreTest.Message);
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, ".restore-test-*.db"));
    }

    [Fact]
    public async Task GuidedRestore_StagesThenAppliesVerifiedDatabaseOnRestartBoundary()
    {
        using var fixture = new OperationsFixture();
        fixture.CreateCommercialDatabase(fixture.ActiveDatabase, "ACTIVE");
        fixture.CreateCommercialDatabase(fixture.SourceDatabase, "RESTORED");
        var sourceService = new BackupService(
            fixture.SourceDatabase,
            fixture.SourceBackupDirectory,
            passwordProvider: () => string.Empty);
        string selectedBackup = await sourceService.CreateBackupAsync("selected");
        BackupService service = fixture.CreateBackupService();

        RestorePreparationResult prepared = await service.PrepareRestoreAsync(selectedBackup);

        Assert.True(prepared.IsReady, prepared.Message);
        Assert.Equal("ACTIVE", fixture.ReadProductName(fixture.ActiveDatabase));
        Assert.True(File.Exists(fixture.PendingDatabase));
        Assert.True(File.Exists(fixture.PendingManifest));
        Assert.NotNull(prepared.SafetyBackupPath);

        Assert.True(await service.ApplyPendingRestoreAsync());
        Assert.Equal("RESTORED", fixture.ReadProductName(fixture.ActiveDatabase));
        Assert.False(File.Exists(fixture.PendingDatabase));
        Assert.False(File.Exists(fixture.PendingManifest));
        Assert.False(await service.ApplyPendingRestoreAsync());
    }

    [Fact]
    public async Task GuidedRestore_RejectsNonCommercialDatabaseWithoutTouchingActiveData()
    {
        using var fixture = new OperationsFixture();
        fixture.CreateCommercialDatabase(fixture.ActiveDatabase, "ACTIVE");
        using (var connection = new SqliteConnection($"Data Source={fixture.SourceDatabase};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated(id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        BackupService service = fixture.CreateBackupService();
        RestorePreparationResult result = await service.PrepareRestoreAsync(fixture.SourceDatabase);

        Assert.False(result.IsReady);
        Assert.Contains("tabla comercial requerida", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ACTIVE", fixture.ReadProductName(fixture.ActiveDatabase));
        Assert.False(File.Exists(fixture.PendingManifest));
    }

    [Fact]
    public async Task AutomaticMaintenance_VerifiesBackupAppliesRetentionAndSkipsUntilDue()
    {
        using var fixture = new OperationsFixture();
        fixture.CreateCommercialDatabase(fixture.ActiveDatabase, "ACTIVE");
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-18T18:00:00Z");
        BackupService service = fixture.CreateBackupService(() => now);
        Directory.CreateDirectory(fixture.BackupDirectory);
        string expired = Path.Combine(fixture.BackupDirectory, "backup_expired.db");
        await File.WriteAllTextAsync(expired, "old");
        File.SetLastWriteTimeUtc(expired, now.AddDays(-40).UtcDateTime);
        var policy = new BackupPolicy(true, TimeSpan.FromHours(24), 2, TimeSpan.FromDays(30), true);

        BackupMaintenanceResult first = await service.RunMaintenanceAsync(policy);
        BackupMaintenanceResult second = await service.RunMaintenanceAsync(policy);

        Assert.True(first.Executed);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(1, first.DeletedBackups);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(first.BackupPath));
        Assert.False(second.Executed);
        Assert.True(File.Exists(fixture.MaintenanceState));
    }

    [Fact]
    public async Task DiagnosticsExport_SanitizesLogsAndNeverPackagesDatabaseOrSecrets()
    {
        using var fixture = new OperationsFixture();
        Directory.CreateDirectory(fixture.LogDirectory);
        Directory.CreateDirectory(fixture.DiagnosticDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.LogDirectory, "ticketfy.log"),
            "password=super-secret Authorization: Bearer abc.def.ghi C:\\Users\\Yersi\\private token?token=url-secret");
        await File.WriteAllTextAsync(fixture.ActiveDatabase, "must-not-be-exported");
        var service = new DiagnosticExportService(
            fixture.LogDirectory,
            fixture.BackupDirectory,
            fixture.ActiveDatabase,
            fixture.DiagnosticDirectory,
            () => DateTimeOffset.Parse("2026-10-18T18:00:00Z"));

        DiagnosticExportResult result = await service.ExportAsync();

        Assert.True(File.Exists(result.ArchivePath));
        Assert.Equal(64, result.Sha256.Length);
        using ZipArchive archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.DoesNotContain(archive.Entries, entry => entry.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archive.Entries, entry => entry.Name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        ZipArchiveEntry logEntry = Assert.Single(archive.Entries,
            entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal));
        using var reader = new StreamReader(logEntry.Open());
        string sanitized = await reader.ReadToEndAsync();
        Assert.DoesNotContain("super-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("url-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Yersi", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telemetry_IsOptInMinimalAndErasedWhenConsentIsRevoked()
    {
        using var fixture = new OperationsFixture();
        var telemetry = new LocalOperationalTelemetry(
            TelemetryConsent.Disabled,
            fixture.TelemetryDirectory,
            () => DateTimeOffset.Parse("2026-10-18T18:00:00Z"));

        await telemetry.TrackAsync("application.started", true);
        Assert.False(Directory.Exists(fixture.TelemetryDirectory));

        telemetry.SetConsent(TelemetryConsent.AnonymousOperational);
        await telemetry.TrackAsync("backup.maintenance", false);
        string file = Assert.Single(Directory.EnumerateFiles(fixture.TelemetryDirectory, "*.jsonl"));
        string content = await File.ReadAllTextAsync(file);
        Assert.Contains("backup.maintenance", content, StringComparison.Ordinal);
        Assert.Contains("false", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machine", content, StringComparison.OrdinalIgnoreCase);

        telemetry.SetConsent(TelemetryConsent.Disabled);
        Assert.Empty(Directory.EnumerateFiles(fixture.TelemetryDirectory, "*.jsonl"));
    }

    [Fact]
    public void LogSanitizer_RedactsBearerQuerySecretsAndWindowsUserProfile()
    {
        string result = LogSanitizer.Sanitize(
            "Bearer very-secret-token Authorization: Basic dXNlcjpwYXNzd29yZA== " +
            "https://local/?api_key=query-secret C:\\Users\\Operator\\Ticketfy");

        Assert.DoesNotContain("very-secret-token", result, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("dXNlcjpwYXNzd29yZA", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Operator", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupMaintenanceWorker_RunsPeriodicallyAndReportsMinimalTelemetry()
    {
        var backup = new RecordingBackupService();
        var telemetry = new RecordingTelemetry();
        var clock = new ManualMaintenanceClock();
        await using var worker = new BackupMaintenanceWorker(
            backup,
            BackupPolicy.Default,
            telemetry,
            clock: clock);

        worker.Start();
        Assert.Equal(0, backup.CallCount);
        clock.Tick();
        // Only a deadlock guard; the test advances the timer explicitly instead of waiting for wall time.
        await backup.Executed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await telemetry.Executed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(backup.CallCount >= 1);
        Assert.Equal("backup.maintenance", telemetry.EventName);
        Assert.True(telemetry.Succeeded);
    }

    private sealed class RecordingBackupService : IBackupService
    {
        public int CallCount { get; private set; }
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BackupMaintenanceResult> RunMaintenanceAsync(BackupPolicy policy, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Executed.TrySetResult();
            return Task.FromResult(new BackupMaintenanceResult(true, true, "ok"));
        }

        public Task<bool> ApplyPendingRestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string> CreateBackupAsync(string reference = "Manual", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CreateZCutBackupAsync(string shiftReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestorePreparationResult> PrepareRestoreAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupValidationResult> TestRestoreAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupValidationResult> ValidateBackupAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingTelemetry : IOperationalTelemetry
    {
        public TelemetryConsent Consent => TelemetryConsent.AnonymousOperational;
        public string? EventName { get; private set; }
        public bool Succeeded { get; private set; }
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetConsent(TelemetryConsent consent) { }

        public Task TrackAsync(string eventName, bool succeeded, CancellationToken cancellationToken = default)
        {
            EventName = eventName;
            Succeeded = succeeded;
            Executed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class OperationsFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"ticketfy-stage5-{Guid.NewGuid():N}");
        public string ActiveDatabase => Path.Combine(Root, "active.db");
        public string SourceDatabase => Path.Combine(Root, "source.db");
        public string BackupDirectory => Path.Combine(Root, "backups");
        public string SourceBackupDirectory => Path.Combine(Root, "source-backups");
        public string PendingDatabase => Path.Combine(Root, "pending.db");
        public string PendingManifest => Path.Combine(Root, "pending.json");
        public string MaintenanceState => Path.Combine(Root, "maintenance.json");
        public string LogDirectory => Path.Combine(Root, "logs");
        public string DiagnosticDirectory => Path.Combine(Root, "diagnostics");
        public string TelemetryDirectory => Path.Combine(Root, "telemetry");

        public OperationsFixture() => Directory.CreateDirectory(Root);

        public BackupService CreateBackupService(Func<DateTimeOffset>? clock = null) => new(
            ActiveDatabase,
            BackupDirectory,
            passwordProvider: () => string.Empty,
            utcNow: clock,
            pendingRestorePath: PendingDatabase,
            pendingManifestPath: PendingManifest,
            maintenanceStatePath: MaintenanceState);

        public void CreateCommercialDatabase(string path, string productName)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE products(id TEXT PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE sales(id TEXT PRIMARY KEY);
                CREATE TABLE usuarios(id TEXT PRIMARY KEY);
                CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO products(id, name) VALUES ('P1', $name);
                """;
            command.Parameters.AddWithValue("$name", productName);
            command.ExecuteNonQuery();
        }

        public string ReadProductName(string path)
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM products WHERE id='P1';";
            return (string)command.ExecuteScalar()!;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
