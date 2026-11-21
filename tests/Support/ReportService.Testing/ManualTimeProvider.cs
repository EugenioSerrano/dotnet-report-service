namespace ReportService.Testing;

/// <summary>
/// A clock the test moves itself: it never follows the wall clock, so nothing in a
/// case waits for real time. Timers are kept against the same clock, which is what
/// makes a poll interval, a keep-alive tick or a bounded wait decidable by the test
/// instead of by the machine's speed.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    /// <summary>Creates the clock at <paramref name="now"/>.</summary>
    /// <param name="now">The instant the clock starts at.</param>
    public ManualTimeProvider(DateTimeOffset now) => _now = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    /// <summary>The timers this clock owns, so a case can see how many are still waiting.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.IsArmed);
            }
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Arm(_now, dueTime, period);
        }

        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that became due.</summary>
    /// <param name="by">How far to move it.</param>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), "the clock only moves forward.");
        }

        List<(TimerCallback Callback, object? State)> due = [];
        lock (_gate)
        {
            _now += by;
            foreach (var timer in _timers.ToArray())
            {
                if (timer.TakeDue(_now) is { } callback)
                {
                    due.Add((callback, timer.State));
                }
            }
        }

        foreach (var (callback, state) in due)
        {
            callback(state);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset _due;
        private TimeSpan _period;
        private bool _armed;

        public object? State => state;

        public bool IsArmed
        {
            get
            {
                lock (owner._gate)
                {
                    return _armed;
                }
            }
        }

        public void Arm(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                _armed = false;
                return;
            }

            _due = now + dueTime;
            _armed = true;
        }

        public TimerCallback? TakeDue(DateTimeOffset now)
        {
            if (!_armed || now < _due)
            {
                return null;
            }

            // A one-shot timer fires once and stops; a periodic one waits for its next period.
            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
            {
                _due = now + _period;
            }
            else
            {
                _armed = false;
            }

            return callback;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Arm(owner._now, dueTime, period);
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _armed = false;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
