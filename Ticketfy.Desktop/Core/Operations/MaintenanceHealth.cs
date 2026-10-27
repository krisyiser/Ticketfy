namespace Ticketfy.Core.Operations;

public enum MaintenanceTask { Audit, Backups, StockAlerts, Recommendations }
public enum MaintenanceState { Pending, Running, Healthy, Failed, Suspended, Disabled, Stopped }
public enum MaintenanceFailure { None, VerificationFailed, ExecutionFailed, AwaitingAudit }
public sealed record MaintenanceTaskStatus(MaintenanceTask Task, MaintenanceState State,
    DateTimeOffset? LastAttemptUtc = null, DateTimeOffset? LastSuccessUtc = null,
    DateTimeOffset? LastFailureUtc = null, MaintenanceFailure Failure = MaintenanceFailure.None);
public sealed record MaintenanceSnapshot(long Revision, bool WritesBlocked, bool RestoreTestEnabled,
    IReadOnlyList<MaintenanceTaskStatus> Tasks);

/// <summary>Process-local evidence from maintenance, never caller supplied messages, paths or credentials.</summary>
public sealed class MaintenanceHealth(TimeProvider? clock = null)
{
    internal static readonly TimeSpan MaximumAuditAge = TimeSpan.FromHours(26);
    public const string WriteBlockedMessage =
        "No se puede verificar la auditoría. Las escrituras están bloqueadas; conserve los datos y solicite soporte. " +
        "Si ya entregó dinero o envió un cobro, no lo repita: conserve la referencia y concilie el intento original.";
    private readonly object _sync = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<MaintenanceTask, MaintenanceTaskStatus> _tasks =
        Enum.GetValues<MaintenanceTask>().ToDictionary(task => task, task => new MaintenanceTaskStatus(task, MaintenanceState.Pending));
    private long _revision;
    private bool _restoreTestEnabled = true;
    public event Action? Changed;

    public MaintenanceSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var audit = _tasks[MaintenanceTask.Audit];
            var age = audit.LastSuccessUtc is { } success ? _clock.GetUtcNow() - success : TimeSpan.MaxValue;
            bool trusted = (audit.State is MaintenanceState.Healthy or MaintenanceState.Running) &&
                audit.Failure == MaintenanceFailure.None && age >= TimeSpan.Zero && age <= MaximumAuditAge;
            return new(_revision, !trusted, _restoreTestEnabled, _tasks.Values.OrderBy(row => row.Task).ToArray());
        }
    }

    internal void RequireWritesAllowed()
    {
        if (GetSnapshot().WritesBlocked) throw new MaintenanceWriteBlockedException();
    }

    internal void ConfigureBackups(BackupPolicy policy)
    {
        lock (_sync) _restoreTestEnabled = policy.VerifyAfterCreation;
        Set(MaintenanceTask.Backups, row => !policy.Enabled ? row with { State = MaintenanceState.Disabled } : row);
    }
    internal void Started(MaintenanceTask task) => Set(task, row => row with
    { State = MaintenanceState.Running, LastAttemptUtc = _clock.GetUtcNow() });
    internal void Succeeded(MaintenanceTask task) => Set(task, row => row with
    { State = MaintenanceState.Healthy, LastSuccessUtc = _clock.GetUtcNow(), Failure = MaintenanceFailure.None });
    internal void Failed(MaintenanceTask task, MaintenanceFailure failure = MaintenanceFailure.ExecutionFailed) => Set(task, row => row with
    { State = MaintenanceState.Failed, LastFailureUtc = _clock.GetUtcNow(), Failure = failure });
    internal void Suspended(MaintenanceTask task) => Set(task, row => row with
    { State = MaintenanceState.Suspended, Failure = MaintenanceFailure.AwaitingAudit });
    internal void Stopped(MaintenanceTask task) => Set(task, row => row with { State = MaintenanceState.Stopped });
    internal void BackupResult(BackupPolicy policy, BackupMaintenanceResult result)
    {
        if (!policy.Enabled) { ConfigureBackups(policy); return; }
        if (!result.Succeeded) Failed(MaintenanceTask.Backups);
        else if (result.Executed)
        {
            Succeeded(MaintenanceTask.Backups);
        }
        else Set(MaintenanceTask.Backups, row => row with
        {
            State = row.Failure != MaintenanceFailure.None ? MaintenanceState.Failed :
                row.LastSuccessUtc is null ? MaintenanceState.Pending : MaintenanceState.Healthy
        });
    }
    private void Set(MaintenanceTask task, Func<MaintenanceTaskStatus, MaintenanceTaskStatus> update)
    {
        lock (_sync) { _tasks[task] = update(_tasks[task]); ++_revision; }
        // A faulty observer must neither suppress the safety state nor stop periodic maintenance.
        foreach (Action observer in Changed?.GetInvocationList() ?? [])
            try { observer(); } catch (Exception ex) { Serilog.Log.Warning(ex, "Maintenance status observer failed"); }
    }
}

public sealed class MaintenanceWriteBlockedException() : InvalidOperationException(MaintenanceHealth.WriteBlockedMessage);
