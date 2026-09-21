namespace BattleNetPrefill.Test;

internal sealed class CacheStatusClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ClockTimer> _timers = new();
    private DateTimeOffset _utcNow;

    internal CacheStatusClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow.ToUniversalTime();
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcDateTime.Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ClockTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
            timer.Change(dueTime, period);
        }
        return timer;
    }

    internal void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        List<ClockTimer> due;
        lock (_sync)
        {
            _utcNow += amount;
            due = _timers.Where(timer => timer.TakeDue(_utcNow)).ToList();
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ClockTimer timer)
    {
        lock (_sync)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ClockTimer(
        CacheStatusClock clock,
        TimerCallback callback,
        object? state) : ITimer
    {
        private DateTimeOffset? _dueAtUtc;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            }
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(period));
            }

            lock (clock._sync)
            {
                if (_disposed)
                {
                    return false;
                }
                _dueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : clock._utcNow + dueTime;
                _period = period;
                return true;
            }
        }

        internal bool TakeDue(DateTimeOffset utcNow)
        {
            if (_disposed || !_dueAtUtc.HasValue || _dueAtUtc.Value > utcNow)
            {
                return false;
            }

            _dueAtUtc = _period == Timeout.InfiniteTimeSpan ? null : utcNow + _period;
            return true;
        }

        internal void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _dueAtUtc = null;
            }
            clock.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
