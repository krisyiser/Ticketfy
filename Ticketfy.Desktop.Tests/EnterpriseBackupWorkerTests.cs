using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Ticketfy.Core.Operations;
using Ticketfy.Core.Startup;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseBackupWorkerTests
{
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(30);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(DeadlockGuard);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(4294967295)]
    public void InvalidTimerIntervalsAreRejectedBeforeStarting(double milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupMaintenanceWorker(
            new BackupStub(), BackupPolicy.Default, new TelemetryStub(), TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task ConcurrentStartCreatesOneTimerAndNeverOverlapsMaintenance()
    {
        var first = Signal();
        var release = Signal();
        var clock = new ManualMaintenanceClock();
        var telemetry = new TelemetryStub();
        var backup = new BackupStub(async (call, token) =>
        {
            if (call == 1)
            {
                first.SetResult();
                await release.Task.WaitAsync(token);
            }
            return new(true, true, "synthetic");
        });
        await using var worker = new BackupMaintenanceWorker(backup, BackupPolicy.Default, telemetry, clock: clock);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, 24), (_, _) => { worker.Start(); return ValueTask.CompletedTask; });
            Assert.Equal(1, clock.TimerCount);
            Assert.Equal(0, backup.Count);
            clock.Tick();
            await Wait(first.Task);
            for (int i = 0; i < 10; i++) clock.Tick();
            Assert.Equal(1, backup.Count);
            release.SetResult();
            Assert.True(await telemetry.Next());
            Assert.True(await telemetry.Next());
            await worker.DisposeAsync();
            Assert.Equal(2, backup.Count); // Missed ticks coalesce; no unbounded backlog.
            Assert.True(worker.Completion.IsCompletedSuccessfully);
            Assert.True(clock.AllDisposed);
            clock.Tick();
            Assert.Equal(2, backup.Count);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("io")]
    [InlineData("invalid-data")]
    [InlineData("foreign-cancellation")]
    public async Task FailedAttemptReportsFailureAndTheNextIntervalStillRuns(string failure)
    {
        var clock = new ManualMaintenanceClock();
        var telemetry = new TelemetryStub();
        var backup = new BackupStub((call, _) =>
        {
            if (call == 1) throw failure switch
            {
                "io" => new IOException("synthetic I/O failure"),
                "invalid-data" => new InvalidDataException("synthetic state failure"),
                _ => new OperationCanceledException("not the worker shutdown")
            };
            return Task.FromResult(new BackupMaintenanceResult(true, true, "synthetic"));
        });
        await using var worker = new BackupMaintenanceWorker(backup, BackupPolicy.Default, telemetry, clock: clock);
        worker.Start();
        clock.Tick();
        Assert.False(await telemetry.Next());
        Assert.False(worker.Completion.IsCompleted);
        clock.Tick();
        Assert.True(await telemetry.Next());
        Assert.Equal(2, backup.Count);
    }

    [Fact]
    public async Task TelemetryFailureDoesNotStopBackupsOrRewriteTheirOutcome()
    {
        var attempted = Signal();
        var clock = new ManualMaintenanceClock();
        var telemetry = new TelemetryStub((call, _, _) =>
        {
            if (call == 1)
            {
                attempted.SetResult();
                throw new InvalidOperationException("synthetic telemetry failure");
            }
            return Task.CompletedTask;
        });
        var backup = new BackupStub();
        await using var worker = new BackupMaintenanceWorker(backup, BackupPolicy.Default, telemetry, clock: clock);
        worker.Start();
        clock.Tick();
        await Wait(attempted.Task);
        clock.Tick();
        Assert.True(await telemetry.Next());
        Assert.Equal(2, backup.Count);
    }

    [Fact]
    public async Task NotDueIsNotReportedAsACompletedBackupAndReturnedFailureIsPreserved()
    {
        var first = Signal();
        var clock = new ManualMaintenanceClock();
        var telemetry = new TelemetryStub();
        var backup = new BackupStub((call, _) =>
        {
            first.TrySetResult();
            return Task.FromResult(new BackupMaintenanceResult(call > 1, false, "synthetic"));
        });
        await using var worker = new BackupMaintenanceWorker(backup, BackupPolicy.Default, telemetry, clock: clock);
        worker.Start();
        clock.Tick();
        await Wait(first.Task);
        clock.Tick();
        Assert.False(await telemetry.Next());
        await worker.DisposeAsync();
        Assert.Equal(1, telemetry.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeCancelsInFlightWorkAndWaitsForItsCleanupBeforeReleasingResources(bool failingCallback)
    {
        var entered = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        var clock = new ManualMaintenanceClock();
        var telemetry = new TelemetryStub();
        var backup = new BackupStub(async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                if (failingCallback) throw new InvalidOperationException("synthetic cancellation callback");
            });
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                await cleanup.Task;
                throw;
            }
            return new(true, true, "unreachable");
        });
        await using var worker = new BackupMaintenanceWorker(backup, BackupPolicy.Default, telemetry, clock: clock);
        try
        {
            worker.Start();
            clock.Tick();
            await Wait(entered.Task);
            Task shutdown = worker.DisposeAsync().AsTask();
            await Wait(cancelled.Task);
            Assert.False(shutdown.IsCompleted);
            Assert.Same(shutdown, worker.DisposeAsync().AsTask());
            Assert.Throws<ObjectDisposedException>(worker.Start);
            clock.Tick();
            Assert.Equal(1, backup.Count);
            cleanup.SetResult();
            await Wait(shutdown);
            Assert.True(worker.Completion.IsCompletedSuccessfully);
            Assert.True(clock.AllDisposed);
            Assert.Equal(0, telemetry.Count);
            worker.Dispose();
        }
        finally { cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task ShutdownAlsoCancelsPendingTelemetryWithoutSchedulingAnotherBackup()
    {
        var entered = Signal();
        var clock = new ManualMaintenanceClock();
        var telemetry = new TelemetryStub(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var backup = new BackupStub();
        await using var worker = new BackupMaintenanceWorker(backup, BackupPolicy.Default, telemetry, clock: clock);
        worker.Start();
        clock.Tick();
        await Wait(entered.Task);
        await Wait(worker.DisposeAsync().AsTask());
        Assert.True(worker.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, backup.Count);
        Assert.Equal(1, telemetry.Count);
        Assert.True(clock.AllDisposed);
    }

    [Fact]
    public async Task DisposeBeforeStartIsIdempotentAndDoesNotCreateATimer()
    {
        var clock = new ManualMaintenanceClock();
        var worker = new BackupMaintenanceWorker(new BackupStub(), BackupPolicy.Default, new TelemetryStub(), clock: clock);
        Assert.True(worker.Completion.IsCompletedSuccessfully);
        await worker.DisposeAsync();
        worker.Dispose();
        Assert.Throws<ObjectDisposedException>(worker.Start);
        Assert.Equal(0, clock.TimerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationContainerOwnsTheWorkerAndStopsItOnEitherDisposalPath(bool asynchronous)
    {
        var clock = new ManualMaintenanceClock();
        var backup = new BackupStub();
        var telemetry = new TelemetryStub();
        var services = AppBootstrapper.BuildServices(backup, BackupPolicy.Default, new DiagnosticsStub(), telemetry, clock);
        try
        {
            var worker = services.GetRequiredService<BackupMaintenanceWorker>();
            Assert.Same(worker, services.GetRequiredService<BackupMaintenanceWorker>());
            Assert.IsType<UnavailableFacturamaService>(services.GetRequiredService<IFacturamaService>());
            clock.Tick();
            Assert.True(await telemetry.Next());
            if (asynchronous) await services.DisposeAsync();
            else services.Dispose();
            Assert.True(worker.Completion.IsCompletedSuccessfully);
            Assert.True(clock.AllDisposed);
            Assert.Throws<ObjectDisposedException>(worker.Start);
            clock.Tick();
            Assert.Equal(1, backup.Count);
        }
        finally { await services.DisposeAsync(); }
    }

    private sealed class BackupStub(Func<int, CancellationToken, Task<BackupMaintenanceResult>>? run = null) : IBackupService
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task<BackupMaintenanceResult> RunMaintenanceAsync(BackupPolicy policy, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _count);
            return run?.Invoke(call, cancellationToken) ?? Task.FromResult(new BackupMaintenanceResult(true, true, "synthetic"));
        }
        public Task<bool> ApplyPendingRestoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateBackupAsync(string reference = "Manual", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CreateZCutBackupAsync(string shiftReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestorePreparationResult> PrepareRestoreAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupValidationResult> TestRestoreAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupValidationResult> ValidateBackupAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TelemetryStub(Func<int, bool, CancellationToken, Task>? track = null) : IOperationalTelemetry
    {
        private readonly Channel<bool> _events = Channel.CreateUnbounded<bool>();
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public TelemetryConsent Consent => TelemetryConsent.AnonymousOperational;
        public void SetConsent(TelemetryConsent consent) => throw new NotSupportedException();
        public Task<bool> Next() => _events.Reader.ReadAsync().AsTask().WaitAsync(DeadlockGuard);
        public async Task TrackAsync(string eventName, bool succeeded, CancellationToken cancellationToken = default)
        {
            Assert.Equal("backup.maintenance", eventName);
            int call = Interlocked.Increment(ref _count);
            if (track != null) await track(call, succeeded, cancellationToken);
            await _events.Writer.WriteAsync(succeeded, cancellationToken);
        }
    }

    private sealed class DiagnosticsStub : IDiagnosticExportService
    {
        public Task<DiagnosticExportResult> ExportAsync(string? destinationDirectory = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
