using System;
using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Operations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class BackupMaintenanceWorker : IDisposable, IAsyncDisposable
{
    private readonly IBackupService _backupService;
    private readonly BackupPolicy _policy;
    private readonly IOperationalTelemetry _telemetry;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _clock;
    private readonly MaintenanceHealth? _health;
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private Task? _shutdown;
    private bool _disposed;

    public BackupMaintenanceWorker(
        IBackupService backupService,
        BackupPolicy policy,
        IOperationalTelemetry telemetry,
        TimeSpan? pollInterval = null,
        TimeProvider? clock = null,
        MaintenanceHealth? health = null)
    {
        _backupService = backupService;
        _policy = policy;
        _telemetry = telemetry;
        _clock = clock ?? TimeProvider.System;
        _health = health;
        _health?.ConfigureBackups(policy);
        _pollInterval = pollInterval ?? TimeSpan.FromHours(1);
        if (_pollInterval < TimeSpan.FromMilliseconds(1) || _pollInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Establish the timer before returning. No maintenance runs on the caller's context.
            _loop ??= RunAsync(_stopping.Token);
        }
    }

    public Task Completion
    {
        get { lock (_lifecycle) return _loop ?? Task.CompletedTask; }
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(_pollInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                try
                {
                    stopping.ThrowIfCancellationRequested();
                    if (_policy.Enabled) _health?.Started(MaintenanceTask.Backups);
                    BackupMaintenanceResult result = await _backupService.RunMaintenanceAsync(_policy, stopping).ConfigureAwait(false);
                    stopping.ThrowIfCancellationRequested();
                    _health?.BackupResult(_policy, result);
                    if (result.Executed)
                        await TrackSafelyAsync(result.Succeeded, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One failed attempt must not disable every future scheduled backup.
                    Serilog.Log.Error(ex, "Periodic backup maintenance failed; another attempt will run on the next interval");
                    _health?.Failed(MaintenanceTask.Backups);
                    await TrackSafelyAsync(false, stopping).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task TrackSafelyAsync(bool succeeded, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _telemetry.TrackAsync("backup.maintenance", succeeded, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Local operational telemetry could not be recorded");
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            return new ValueTask(_shutdown ??= StopCoreAsync(_loop ?? Task.CompletedTask));
        }
    }

    private async Task StopCoreAsync(Task loop)
    {
        try
        {
            try
            {
                await _stopping.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A subscriber's cancellation callback must not skip the in-flight cleanup.
                Serilog.Log.Warning(ex, "Backup maintenance cancellation callback failed");
            }
            await loop.ConfigureAwait(false);
        }
        finally
        {
            _health?.Stopped(MaintenanceTask.Backups);
            _stopping.Dispose();
        }
    }
}
