using ReportService.Workers.Orchestration.Dispatch;

namespace ReportService.Testing;

/// <summary>
/// The durable requests the tests use in place of the database. It answers the
/// same contract as the real store: a poll claims only what is due, in request
/// order, a bounded number of requests, and holds them in one unit of work whose
/// decisions are stored only when it commits. A test can therefore assert what a
/// refused or abandoned poll wrote — nothing.
/// </summary>
public sealed class InMemoryIntentStore : IIntentStore
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private long _next = 1;

    /// <summary>The requests the store holds, as they are now.</summary>
    public IReadOnlyList<IntentRecord> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(entry => entry.ToRecord())];
            }
        }
    }

    /// <summary>The references recorded for the requests that were dispatched.</summary>
    public IReadOnlyList<ExecutionReference> References
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(entry => entry.Reference).OfType<ExecutionReference>()];
            }
        }
    }

    /// <summary>Records a pending request due at <paramref name="due"/>.</summary>
    /// <param name="kind">What the request asks for.</param>
    /// <param name="subjectId">What it is about.</param>
    /// <param name="due">When the first attempt may run.</param>
    /// <param name="createdAt">When the request was recorded.</param>
    /// <returns>The request's identifier.</returns>
    public long Enqueue(string kind, string subjectId, DateTimeOffset due, DateTimeOffset? createdAt = null)
    {
        lock (_gate)
        {
            var id = _next++;
            _entries.Add(new Entry(id, kind, subjectId, 0, createdAt ?? due, due, Pending: true, null));
            return id;
        }
    }

    /// <summary>The status the store keeps for <paramref name="intentId"/>.</summary>
    /// <param name="intentId">The request's identifier.</param>
    public string StatusOf(long intentId)
    {
        lock (_gate)
        {
            return _entries.Single(entry => entry.Id == intentId).Pending ? "PENDING" : "DISPATCHED";
        }
    }

    /// <inheritdoc />
    public Task<IIntentClaim> ClaimDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        lock (_gate)
        {
            var claimed = _entries
                .Where(entry => entry.Pending && entry.NextAttemptAt <= now)
                .OrderBy(entry => entry.Id)
                .Take(batchSize)
                .Select(entry => entry.ToRecord())
                .ToList();
            return Task.FromResult<IIntentClaim>(new Claim(this, claimed));
        }
    }

    /// <inheritdoc />
    public Task<TimeSpan?> OldestPendingAgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var oldest = _entries.Where(entry => entry.Pending).Select(entry => (DateTimeOffset?)entry.CreatedAt).Min();
            return Task.FromResult(oldest is { } instant ? now - instant : (TimeSpan?)null);
        }
    }

    private void Apply(IReadOnlyList<(long IntentId, bool Dispatched, ExecutionReference? Reference, DateTimeOffset Now, DateTimeOffset? Next)> changes)
    {
        lock (_gate)
        {
            foreach (var change in changes)
            {
                var index = _entries.FindIndex(entry => entry.Id == change.IntentId);
                if (index < 0)
                {
                    continue;
                }

                var entry = _entries[index];
                _entries[index] = change.Dispatched
                    ? entry with { Pending = false, Attempts = entry.Attempts + 1, Reference = change.Reference }
                    : entry with
                    {
                        Attempts = entry.Attempts + 1,
                        NextAttemptAt = change.Next ?? entry.NextAttemptAt,
                    };
            }
        }
    }

    private sealed record Entry(
        long Id,
        string Kind,
        string SubjectId,
        int Attempts,
        DateTimeOffset CreatedAt,
        DateTimeOffset NextAttemptAt,
        bool Pending,
        ExecutionReference? Reference)
    {
        public IntentRecord ToRecord() => new(Id, Kind, SubjectId, Attempts, CreatedAt);
    }

    private sealed class Claim(InMemoryIntentStore store, IReadOnlyList<IntentRecord> claimed) : IIntentClaim
    {
        private readonly Dictionary<long, (bool Dispatched, ExecutionReference? Reference, DateTimeOffset Now, DateTimeOffset? Next)> _changes = [];

        public IReadOnlyList<IntentRecord> Intents => claimed;

        public Task MarkDispatchedAsync(long intentId, ExecutionReference reference, DateTimeOffset now, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reference);
            _changes[intentId] = (true, reference, now, null);
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(long intentId, DateTimeOffset now, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
        {
            _changes[intentId] = (false, null, now, nextAttemptAt);
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            store.Apply([.. _changes.Select(change => (change.Key, change.Value.Dispatched, change.Value.Reference, change.Value.Now, change.Value.Next))]);
            _changes.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            // A claim that was not committed stored nothing.
            _changes.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
