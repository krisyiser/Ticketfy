using Microsoft.EntityFrameworkCore;
using Serilog;
using Ticketfy.Data;
using Ticketfy.Services.Audit;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Retention-safe audit monitor. Audit entries are never exported as plaintext
/// or deleted automatically; this worker only verifies the integrity chain.
/// </summary>
public sealed class AuditArchivingWorker
{
    private readonly IDbContextFactory<AuditDbContext> _auditContextFactory;

    public AuditArchivingWorker(IDbContextFactory<AuditDbContext> auditContextFactory) =>
        _auditContextFactory = auditContextFactory;

    public Task StartAsync(CancellationToken stoppingToken)
    {
        _ = Task.Run(() => ExecuteAsync(stoppingToken), stoppingToken);
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var audit = new AuditService(_auditContextFactory);
                if (!await audit.VerifyIntegrityAsync())
                    Log.Fatal("Audit integrity verification failed; administrative history may have been altered");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Audit integrity verification could not be completed");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
