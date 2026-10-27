namespace Ticketfy.Desktop.Tests;

// Drives the single periodic consumer explicitly. No sleeps, OS timers or operational data.
internal sealed class ManualMaintenanceClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    public int TimerCount { get { lock (_timers) return _timers.Count; } }
    public bool AllDisposed { get { lock (_timers) return _timers.All(x => x.IsDisposed); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(dueTime, period);
        var timer = new ManualTimer(callback, state);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public void Tick()
    {
        ManualTimer[] timers;
        lock (_timers) timers = _timers.ToArray();
        foreach (var timer in timers) timer.Tick();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public void Tick() { if (!IsDisposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
