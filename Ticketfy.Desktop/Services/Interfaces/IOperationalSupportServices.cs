using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Operations;

namespace Ticketfy.Services.Interfaces;

public interface IDiagnosticExportService
{
    Task<DiagnosticExportResult> ExportAsync(string? destinationDirectory = null, CancellationToken cancellationToken = default);
}

public interface IOperationalTelemetry
{
    TelemetryConsent Consent { get; }
    void SetConsent(TelemetryConsent consent);
    Task TrackAsync(string eventName, bool succeeded, CancellationToken cancellationToken = default);
}
