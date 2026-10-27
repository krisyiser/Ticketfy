using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ticketfy.Core.Operations;
using Ticketfy.Core.Startup;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseBackupWorkerTests
{
    [Fact]
    public async Task OwnedWorkers_ParallelStartsOwnOneTaskAndStopTheInflightCycle()
    {
        var entered = Signal(); var canceled = Signal();
        int calls = 0; var clock = new ManualMaintenanceClock();
        await using var worker = new PeriodicStub(async token =>
        {
            Assert.Null(SynchronizationContext.Current); Interlocked.Increment(ref calls); entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled.TrySetResult(); }
        }, clock);
        await Parallel.ForEachAsync(Enumerable.Range(0, 32), (_, _) => { worker.Start(); return ValueTask.CompletedTask; });
        await Wait(entered.Task); Assert.Equal(1, calls); Assert.Equal(1, clock.TimerCount);
        clock.Tick(); clock.Tick(); Assert.Equal(1, calls);
        await Wait(worker.DisposeAsync().AsTask()); await Wait(canceled.Task);
        Assert.True(worker.Completion.IsCompletedSuccessfully); Assert.True(clock.AllDisposed);
        Assert.Throws<ObjectDisposedException>(worker.Start);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedWorkers_FailedCycleIsObservableAndNextIntervalRetries(bool foreignCancellation)
    {
        var first = Signal(); var second = Signal(); int calls = 0;
        var clock = new ManualMaintenanceClock();
        PeriodicStub? worker = null;
        worker = new PeriodicStub(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { first.SetResult(); throw foreignCancellation ? new OperationCanceledException("Foreign token") : new IOException("Synthetic failure"); }
            Assert.NotNull(worker!.LastFailureType); second.TrySetResult(); return Task.CompletedTask;
        }, clock);
        await using (worker)
        {
            worker.Start(); await Wait(first.Task); clock.Tick(); await Wait(second.Task);
            await worker.DisposeAsync();
            Assert.Null(worker.LastFailureType); Assert.Equal(2, calls);
        }
    }

    [Fact]
    public async Task OwnedWorkers_ThrowingCancellationCallbackDoesNotSkipCleanup()
    {
        var entered = Signal(); var disposed = Signal();
        await using var worker = new PeriodicStub(async token =>
        {
            using var registration = token.Register(() => throw new InvalidOperationException("Synthetic callback"));
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { disposed.SetResult(); }
        }, new ManualMaintenanceClock());
        worker.Start(); await Wait(entered.Task);
        await Wait(worker.DisposeAsync().AsTask()); await Wait(disposed.Task);
        Assert.True(worker.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task OwnedWorkers_DisposeBeforeStartAndUnexpectedFailureAreObserved()
    {
        var notStarted = new FatalWorker();
        notStarted.Dispose(); await notStarted.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(notStarted.Start);
        await using var failed = new FatalWorker(); failed.Start(); await Wait(failed.Completion);
        Assert.Equal(typeof(IOException).FullName, failed.LastFailureType);
        await failed.DisposeAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295)]
    public void OwnedWorkers_InvalidIntervalsNeverStart(double milliseconds)
    {
        using var verification = new AuditVerificationCoordinator(new AuditWorkerStub(_ => Task.FromResult(true)), new MaintenanceHealth());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuditArchivingWorker(verification,
            TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task AuditWorker_FailedIntegrityIsReportedAndSubsequentCycleCanVerifyAgain()
    {
        int attempts = 0; var first = Signal(); var second = Signal(); var clock = new ManualMaintenanceClock();
        var audit = new AuditWorkerStub(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { first.SetResult(); return Task.FromResult(false); }
            second.SetResult(); return Task.FromResult(true);
        });
        await using var verification = new AuditVerificationCoordinator(audit, new MaintenanceHealth(clock));
        await using var worker = new AuditArchivingWorker(verification, clock: clock);
        worker.Start(); await Wait(first.Task); clock.Tick(); await Wait(second.Task); await worker.DisposeAsync();
        Assert.Equal(2, attempts); Assert.Null(worker.LastFailureType); Assert.True(clock.AllDisposed);
    }

    [Fact]
    public async Task RecommendationWake_IsBoundedCoalescedAndCancellableWithoutLosingDurableWork()
    {
        var queue = new CoOccurrenceQueue();
        await Parallel.ForEachAsync(Enumerable.Range(0, 100000), (_, _) => { queue.Signal(); return ValueTask.CompletedTask; });
        Assert.Equal(1, queue.PendingSignals);
        await queue.WaitAsync(TimeSpan.FromHours(1), TimeProvider.System, default); Assert.Equal(0, queue.PendingSignals);
        await Wait(queue.WaitAsync(TimeSpan.Zero, TimeProvider.System, default));
        using var cancel = new CancellationTokenSource();
        var pending = queue.WaitAsync(TimeSpan.FromHours(1), TimeProvider.System, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        queue.Signal(); Assert.Equal(1, queue.PendingSignals);
        await queue.WaitAsync(TimeSpan.FromHours(1), TimeProvider.System, default); Assert.Equal(0, queue.PendingSignals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationContainer_OwnsAllWorkersAndStopsThemWithoutOpeningOperationalStorage(bool asynchronous)
    {
        var factory = new BlockingWorkerFactory();
        var auditEntered = Signal(); var auditStopped = Signal();
        var audit = new AuditWorkerStub(async token =>
        {
            auditEntered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }
            finally { auditStopped.SetResult(); }
        });
        var auditFactory = new AppDbContextFactoryImpl<AuditDbContext>(new DbContextOptionsBuilder<AuditDbContext>().UseSqlite("Data Source=:memory:").Options);
        var provider = AppBootstrapper.BuildServices(new BackupStub(), BackupPolicy.Default, new DiagnosticsStub(), new TelemetryStub(),
            background: new(factory, auditFactory, audit));
        try
        {
            var alerts = provider.GetRequiredService<AlertBackgroundWorker>();
            var recommendations = provider.GetRequiredService<CoOccurrenceWorker>();
            var integrity = provider.GetRequiredService<AuditArchivingWorker>();
            var backups = provider.GetRequiredService<BackupMaintenanceWorker>();
            Assert.Same(factory, provider.GetRequiredService<IDbContextFactory<AppDbContext>>());
            Assert.Same(audit, provider.GetRequiredService<IAuditService>());
            Assert.IsType<UnavailableFacturamaService>(provider.GetRequiredService<IFacturamaService>());
            await Wait(factory.BothEntered.Task); await Wait(auditEntered.Task);
            if (asynchronous) await provider.DisposeAsync(); else provider.Dispose();
            await Wait(auditStopped.Task);
            Assert.True(alerts.Completion.IsCompletedSuccessfully); Assert.True(recommendations.Completion.IsCompletedSuccessfully);
            Assert.True(integrity.Completion.IsCompletedSuccessfully); Assert.True(backups.Completion.IsCompletedSuccessfully);
            Assert.Equal(2, factory.Attempts); Assert.Equal(2, factory.Stopped);
            Assert.Throws<ObjectDisposedException>(recommendations.Start);
        }
        finally { await provider.DisposeAsync(); }
    }

    private sealed class PeriodicStub(Func<CancellationToken, Task> action, TimeProvider clock)
        : PeriodicBackgroundWorker(TimeSpan.FromHours(1), clock)
    {
        internal override Task RunOnceAsync(CancellationToken token) => action(token);
    }
    private sealed class FatalWorker : OwnedBackgroundWorker
    {
        protected override Task ExecuteAsync(CancellationToken token) => throw new IOException("Synthetic fatal loop failure");
    }
    private sealed class BlockingWorkerFactory : IDbContextFactory<AppDbContext>
    {
        public TaskCompletionSource BothEntered { get; } = Signal();
        private int _attempts, _stopped;
        public int Attempts => Volatile.Read(ref _attempts);
        public int Stopped => Volatile.Read(ref _stopped);
        public AppDbContext CreateDbContext() => throw new NotSupportedException();
        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) == 2) BothEntered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("Unreachable"); }
            finally { Interlocked.Increment(ref _stopped); }
        }
    }
    private sealed class AuditWorkerStub(Func<CancellationToken, Task<bool>> verify) : IAuditService
    {
        public Task<bool> VerifyIntegrityAsync(CancellationToken cancellationToken = default) => verify(cancellationToken);
        public Task LogAsync(AuditLogEntity log) => throw new NotSupportedException();
        public Task LogAsync(string level, string message, string? meta = null) => throw new NotSupportedException();
        public Task<List<AuditLogEntity>> GetRecentLogsAsync(int limit = 100) => throw new NotSupportedException();
    }
}
