namespace Ticketfy.Services.Implementations;

/// <summary>Verifies the chain; never exports or deletes administrative history.</summary>
public sealed class AuditArchivingWorker(AuditVerificationCoordinator verification, TimeSpan? interval = null, TimeProvider? clock = null)
    : PeriodicBackgroundWorker(interval ?? TimeSpan.FromHours(24), clock)
{
    internal override async Task RunOnceAsync(CancellationToken token)
    {
        if (!await verification.VerifyAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("La verificación de integridad de auditoría no coincide.");
    }
}
