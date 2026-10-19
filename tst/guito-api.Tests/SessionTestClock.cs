namespace GuitoApi.Tests;

internal sealed class SessionTestClock : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private TimeSpan _elapsed;
    public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override long GetTimestamp() => _elapsed.Ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers)
        {
            var timer = new Timer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    public void Advance(TimeSpan amount)
    {
        List<Timer> due;
        lock (_timers)
        {
            _elapsed += amount;
            due = _timers.Where(timer => timer.Due <= _elapsed).ToList();
            foreach (var timer in due) timer.Due = TimeSpan.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
    }

    public async Task WaitForTimerAsync(int seconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (_timers)
                if (_timers.Any(timer => timer.Due - _elapsed == TimeSpan.FromSeconds(seconds))) return;
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class Timer(SessionTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan Due { get; set; } = TimeSpan.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers) Due = dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : clock._elapsed + dueTime;
            return true;
        }
        public void Fire() => callback(state);
        public void Dispose() { lock (clock._timers) Due = TimeSpan.MaxValue; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
