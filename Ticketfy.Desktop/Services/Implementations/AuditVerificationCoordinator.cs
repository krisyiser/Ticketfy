using Ticketfy.Core.Operations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

/// <summary>One owned, read-only verification shared by the periodic worker and the operator's retry.</summary>
public sealed class AuditVerificationCoordinator(IAuditService audit, MaintenanceHealth health) : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private Task<bool>? _verification;
    private Task? _shutdown;
    private bool _disposed;

    public Task<bool> VerifyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_verification is null || _verification.IsCompleted)
                _verification = Task.Run(() => VerifyCoreAsync(_stop.Token));
            // Canceling a UI waiter must not cancel verification requested by other consumers.
            return _verification.WaitAsync(cancellationToken);
        }
    }
    private async Task<bool> VerifyCoreAsync(CancellationToken token)
    {
        health.Started(MaintenanceTask.Audit);
        try
        {
            bool valid = await audit.VerifyIntegrityAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (valid) health.Succeeded(MaintenanceTask.Audit);
            else health.Failed(MaintenanceTask.Audit, MaintenanceFailure.VerificationFailed);
            return valid;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            health.Failed(MaintenanceTask.Audit, MaintenanceFailure.VerificationFailed);
            Serilog.Log.Error(ex, "Audit integrity verification could not complete");
            return false;
        }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            return new(_shutdown ??= StopAsync(_verification ?? Task.CompletedTask));
        }
    }
    private async Task StopAsync(Task verification)
    {
        try
        {
            try { await _stop.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Audit verification cancellation callback failed"); }
            await verification.ConfigureAwait(false);
        }
        finally { health.Stopped(MaintenanceTask.Audit); _stop.Dispose(); }
    }
}
