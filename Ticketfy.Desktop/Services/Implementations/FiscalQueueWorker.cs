using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class FiscalQueueWorker
{
    private readonly IFiscalIntegrationQueueService _queue;
    private readonly TimeSpan _idleDelay;

    public FiscalQueueWorker(IFiscalIntegrationQueueService queue, TimeSpan? idleDelay = null)
    {
        _queue = queue;
        _idleDelay = idleDelay ?? TimeSpan.FromSeconds(30);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                bool processed = await _queue.ProcessNextAsync(cancellationToken);
                if (!processed) await Task.Delay(_idleDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fiscal queue iteration failed");
                await Task.Delay(_idleDelay, cancellationToken);
            }
        }
    }
}
