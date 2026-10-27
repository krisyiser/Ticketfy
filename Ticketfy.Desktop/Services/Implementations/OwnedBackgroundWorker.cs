using Ticketfy.Core.Operations;

namespace Ticketfy.Services.Implementations;

/// <summary>One owned task, never on the UI context, with cancellation and observed shutdown.</summary>
public abstract class OwnedBackgroundWorker(MaintenanceHealth? health = null, MaintenanceTask task = MaintenanceTask.StockAlerts) : IDisposable, IAsyncDisposable
{
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private Task? _shutdown;
    private bool _disposed;
    private string? _lastFailureType;
    public string? LastFailureType => Volatile.Read(ref _lastFailureType);
    public Task Completion { get { lock (_lifecycle) return _run ?? Task.CompletedTask; } }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _run ??= Task.Run(() => RunGuardedAsync(_stop.Token));
        }
    }
    private async Task RunGuardedAsync(CancellationToken token)
    {
        try { await ExecuteAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { RecordFailure(ex); }
    }
    protected void RecordFailure(Exception exception)
    {
        Volatile.Write(ref _lastFailureType, exception.GetType().FullName);
        if (exception is MaintenanceWriteBlockedException) health?.Suspended(task);
        else health?.Failed(task);
        Serilog.Log.Error(exception, "Background worker {Worker} failed", GetType().Name);
    }
    protected void BeginCycle() => health?.Started(task);
    protected void ClearFailure()
    {
        Volatile.Write(ref _lastFailureType, null);
        health?.Succeeded(task);
    }
    protected abstract Task ExecuteAsync(CancellationToken token);
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            return new(_shutdown ??= StopAsync(_run ?? Task.CompletedTask));
        }
    }
    private async Task StopAsync(Task run)
    {
        try
        {
            try { await _stop.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Background cancellation callback failed"); }
            await run.ConfigureAwait(false);
        }
        finally { health?.Stopped(task); _stop.Dispose(); }
    }
}

public abstract class PeriodicBackgroundWorker : OwnedBackgroundWorker
{
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    protected PeriodicBackgroundWorker(TimeSpan interval, TimeProvider? clock = null,
        MaintenanceHealth? health = null, MaintenanceTask task = MaintenanceTask.StockAlerts) : base(health, task)
    {
        if (interval < TimeSpan.FromMilliseconds(1) || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval; _clock = clock ?? TimeProvider.System;
    }
    protected sealed override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_interval, _clock);
        do
        {
            token.ThrowIfCancellationRequested();
            try { BeginCycle(); await RunOnceAsync(token).ConfigureAwait(false); ClearFailure(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { RecordFailure(ex); }
        } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }
    internal abstract Task RunOnceAsync(CancellationToken token);
}
