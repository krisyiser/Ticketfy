using System.IO.Compression;
using System.Text.Json;
using Ticketfy.Core.Operations;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseBackupWorkerTests
{
    [Fact]
    public void MaintenanceHealth_UnknownFailedStaleAndReversedClockBlockWritesWithoutChangingEvidence()
    {
        var clock = new HealthClock();
        var health = new MaintenanceHealth(clock);
        Assert.True(health.GetSnapshot().WritesBlocked);
        Assert.Throws<MaintenanceWriteBlockedException>(health.RequireWritesAllowed);
        health.Started(MaintenanceTask.Audit);
        Assert.True(health.GetSnapshot().WritesBlocked);
        health.Succeeded(MaintenanceTask.Audit);
        var previous = health.GetSnapshot();
        Assert.False(previous.WritesBlocked);
        health.Started(MaintenanceTask.Audit);
        Assert.False(health.GetSnapshot().WritesBlocked);
        clock.Now += MaintenanceHealth.MaximumAuditAge + TimeSpan.FromTicks(1);
        Assert.True(health.GetSnapshot().WritesBlocked);
        clock.Now -= MaintenanceHealth.MaximumAuditAge + TimeSpan.FromSeconds(1);
        Assert.True(health.GetSnapshot().WritesBlocked);
        health.Succeeded(MaintenanceTask.Audit);
        health.Failed(MaintenanceTask.Audit, MaintenanceFailure.VerificationFailed);
        health.Started(MaintenanceTask.Audit);
        Assert.True(health.GetSnapshot().WritesBlocked); // A retry is not proof of recovery.
        Assert.Equal(MaintenanceState.Healthy, previous.Tasks.Single(x => x.Task == MaintenanceTask.Audit).State);
        health.Succeeded(MaintenanceTask.Audit); health.RequireWritesAllowed();
        health.Stopped(MaintenanceTask.Audit);
        Assert.True(health.GetSnapshot().WritesBlocked);
    }

    [Fact]
    public void MaintenanceHealth_BackupSkipAndObserversCannotClearFailureOrChangeWriteSafety()
    {
        var health = new MaintenanceHealth();
        int notifications = 0;
        health.Changed += () => throw new InvalidOperationException("Synthetic observer");
        health.Changed += () => notifications++;
        health.Succeeded(MaintenanceTask.Audit);
        health.BackupResult(BackupPolicy.Default, new(false, true, "not due"));
        Assert.Equal(MaintenanceState.Pending, BackupStatus(health).State);
        health.BackupResult(BackupPolicy.Default, new(true, false, "password=never-expose"));
        health.Started(MaintenanceTask.Backups);
        health.BackupResult(BackupPolicy.Default, new(false, true, "not due"));
        Assert.Equal(MaintenanceState.Failed, BackupStatus(health).State);
        Assert.False(health.GetSnapshot().WritesBlocked);
        health.BackupResult(BackupPolicy.Default, new(true, true, "done"));
        health.BackupResult(BackupPolicy.Default, new(false, true, "not due"));
        Assert.Equal(MaintenanceState.Healthy, BackupStatus(health).State);
        health.BackupResult(BackupPolicy.Default with { Enabled = false, VerifyAfterCreation = false }, new(false, true, "disabled"));
        Assert.Equal(MaintenanceState.Disabled, BackupStatus(health).State);
        Assert.False(health.GetSnapshot().RestoreTestEnabled);
        health.Suspended(MaintenanceTask.StockAlerts);
        Assert.Equal(MaintenanceFailure.AwaitingAudit, health.GetSnapshot().Tasks.Single(x => x.Task == MaintenanceTask.StockAlerts).Failure);
        Assert.True(notifications >= 9);
    }

    [Fact]
    public async Task MaintenanceVerification_CoalescesCallersAndCanceledWaiterDoesNotCancelSharedCheck()
    {
        var entered = Signal(); var release = Signal(); int calls = 0;
        var health = new MaintenanceHealth();
        await using var coordinator = new AuditVerificationCoordinator(new AuditWorkerStub(async token =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            await release.Task.WaitAsync(token); return true;
        }), health);
        using var waiterCancel = new CancellationTokenSource();
        var canceled = coordinator.VerifyAsync(waiterCancel.Token);
        var survivors = Enumerable.Range(0, 32).Select(_ => coordinator.VerifyAsync()).ToArray();
        await Wait(entered.Task); waiterCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.True(health.GetSnapshot().WritesBlocked);
        Assert.Equal(1, calls);
        release.SetResult();
        Assert.All(await Task.WhenAll(survivors), Assert.True);
        Assert.False(health.GetSnapshot().WritesBlocked);
        await coordinator.DisposeAsync(); coordinator.Dispose();
        Assert.True(health.GetSnapshot().WritesBlocked);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.VerifyAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.VerifyAsync(waiterCancel.Token));
    }

    [Fact]
    public async Task MaintenanceVerification_FailureRemainsBlockedUntilFullSuccessfulRetry()
    {
        int attempts = 0;
        var health = new MaintenanceHealth();
        await using var coordinator = new AuditVerificationCoordinator(new AuditWorkerStub(_ =>
        {
            return ++attempts switch
            {
                1 => throw new IOException("synthetic secret exception"),
                2 => Task.FromResult(false),
                _ => Task.FromResult(true)
            };
        }), health);
        Assert.False(await coordinator.VerifyAsync()); Assert.True(health.GetSnapshot().WritesBlocked);
        Assert.False(await coordinator.VerifyAsync()); Assert.True(health.GetSnapshot().WritesBlocked);
        Assert.True(await coordinator.VerifyAsync()); Assert.False(health.GetSnapshot().WritesBlocked);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task MaintenanceVerification_DisposalCancelsOwnedCheckAndAwaitsCleanupEvenIfCallbackThrows()
    {
        var entered = Signal(); var exited = Signal();
        var health = new MaintenanceHealth();
        await using var coordinator = new AuditVerificationCoordinator(new AuditWorkerStub(async token =>
        {
            using var registration = token.Register(() => throw new IOException("Synthetic cancellation callback"));
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }
            finally { exited.SetResult(); }
        }), health);
        var check = coordinator.VerifyAsync(); await Wait(entered.Task);
        await Wait(coordinator.DisposeAsync().AsTask()); await Wait(exited.Task);
        Assert.False(await check);
        Assert.Equal(MaintenanceState.Stopped, health.GetSnapshot().Tasks[0].State);
        Assert.True(health.GetSnapshot().WritesBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceBackupWorker_ReportsFailureRetainsItOnSkipAndRecoversOnActualSuccess(bool throws)
    {
        var clock = new ManualMaintenanceClock(); var health = new MaintenanceHealth();
        var telemetry = new TelemetryStub();
        var skipped = Signal();
        health.Changed += () =>
        {
            if (BackupStatus(health).State == MaintenanceState.Failed && BackupStatus(health).LastAttemptUtc is not null)
                skipped.TrySetResult();
        };
        var backup = new BackupStub((attempt, _) =>
        {
            if (attempt == 1 && throws) throw new IOException("Synthetic backup error");
            return Task.FromResult(attempt switch
            {
                1 => new BackupMaintenanceResult(true, false, "synthetic failure"),
                2 => new BackupMaintenanceResult(false, true, "skipped"),
                _ => new BackupMaintenanceResult(true, true, "restored")
            });
        });
        await using var worker = new BackupMaintenanceWorker(backup, BackupPolicy.Default, telemetry, clock: clock, health: health);
        worker.Start(); clock.Tick();
        Assert.False(await telemetry.Next());
        Assert.Equal(MaintenanceState.Failed, BackupStatus(health).State);
        skipped = Signal(); clock.Tick(); await Wait(skipped.Task);
        Assert.Equal(MaintenanceState.Failed, BackupStatus(health).State);
        clock.Tick(); Assert.True(await telemetry.Next());
        Assert.Equal(MaintenanceState.Healthy, BackupStatus(health).State);
        await worker.DisposeAsync();
        Assert.Equal(MaintenanceState.Stopped, BackupStatus(health).State);
    }

    [Fact]
    public async Task MaintenanceDiagnostics_ExportsTypedEvidenceNotExceptionMessagesOrDatabaseContents()
    {
        string root = Path.Combine(Path.GetTempPath(), "ticketfy-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var health = new MaintenanceHealth();
            health.Failed(MaintenanceTask.Audit, MaintenanceFailure.VerificationFailed);
            health.BackupResult(BackupPolicy.Default, new(true, false, "password=synthetic-private-text", @"C:\private\test.db"));
            var export = new DiagnosticExportService(Path.Combine(root, "logs"), Path.Combine(root, "backups"),
                Path.Combine(root, "absent.db"), root, maintenance: health.GetSnapshot);
            var result = await export.ExportAsync();
            using var zip = ZipFile.OpenRead(result.ArchivePath);
            using var reader = new StreamReader(zip.GetEntry("environment.json")!.Open());
            string content = await reader.ReadToEndAsync();
            Assert.DoesNotContain("synthetic-private-text", content);
            Assert.DoesNotContain("private", content);
            using var json = JsonDocument.Parse(content);
            var maintenance = json.RootElement.GetProperty("maintenance");
            Assert.True(maintenance.GetProperty("WritesBlocked").GetBoolean());
            Assert.Equal("VerificationFailed", maintenance.GetProperty("tasks")[0].GetProperty("failure").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static MaintenanceTaskStatus BackupStatus(MaintenanceHealth health) =>
        health.GetSnapshot().Tasks.Single(x => x.Task == MaintenanceTask.Backups);
    private sealed class HealthClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
