using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Operations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class BackupMaintenanceWorker : IDisposable
{
    private readonly IBackupService _backupService;
    private readonly BackupPolicy _policy;
    private readonly IOperationalTelemetry _telemetry;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public BackupMaintenanceWorker(
        IBackupService backupService,
        BackupPolicy policy,
        IOperationalTelemetry telemetry,
        TimeSpan? pollInterval = null)
    {
        _backupService = backupService;
        _policy = policy;
        _telemetry = telemetry;
        _pollInterval = pollInterval ?? TimeSpan.FromHours(1);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public void Start()
    {
        if (_loop != null) return;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                BackupMaintenanceResult result = await _backupService.RunMaintenanceAsync(_policy, _stopping.Token);
                if (result.Executed)
                    await TrackSafelyAsync("backup.maintenance", result.Succeeded, _stopping.Token);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Periodic backup maintenance worker stopped unexpectedly");
        }
    }

    private async Task TrackSafelyAsync(string eventName, bool succeeded, CancellationToken cancellationToken)
    {
        try
        {
            await _telemetry.TrackAsync(eventName, succeeded, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Local operational telemetry could not be recorded");
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
