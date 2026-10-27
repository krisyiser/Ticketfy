using System.Threading.Channels;

namespace Ticketfy.Services.Implementations;

/// <summary>Coalesced wake-up only. Durable work is in recommendation_jobs, never in this channel.</summary>
public sealed class CoOccurrenceQueue
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false });
    public void Signal() => _wake.Writer.TryWrite(true);
    internal int PendingSignals => _wake.Reader.Count;
    internal async Task WaitAsync(TimeSpan interval, TimeProvider clock, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(interval, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { await _wake.Reader.ReadAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
    }
}
