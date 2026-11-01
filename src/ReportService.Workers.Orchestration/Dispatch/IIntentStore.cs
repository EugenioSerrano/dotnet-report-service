namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>One durable request as the dispatcher claimed it.</summary>
/// <param name="IntentId">The request's own identifier.</param>
/// <param name="Kind">What the request asks for.</param>
/// <param name="SubjectId">What the request is about: an upload's identifier.</param>
/// <param name="Attempts">How many dispatch attempts were recorded before this one.</param>
/// <param name="CreatedAt">When the request was recorded, in UTC.</param>
public sealed record IntentRecord(
    long IntentId,
    string Kind,
    string SubjectId,
    int Attempts,
    DateTimeOffset CreatedAt);

/// <summary>
/// The requests claimed by one poll, held in one open unit of work. Nothing the
/// dispatcher decides is stored until the unit commits, so a process that dies
/// mid-poll leaves the requests exactly as they were and the next poll claims them
/// again. Every request stays pending or becomes dispatched — never a failed state.
/// </summary>
public interface IIntentClaim : IAsyncDisposable
{
    /// <summary>The claimed requests, in request order.</summary>
    IReadOnlyList<IntentRecord> Intents { get; }

    /// <summary>Records that <paramref name="intentId"/> was dispatched as <paramref name="reference"/>.</summary>
    /// <param name="intentId">The request's identifier.</param>
    /// <param name="reference">The execution that serves it.</param>
    /// <param name="now">When the dispatch happened, in UTC.</param>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    Task MarkDispatchedAsync(long intentId, ExecutionReference reference, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records a failed attempt and when the next one is due.</summary>
    /// <param name="intentId">The request's identifier.</param>
    /// <param name="now">When the attempt failed, in UTC.</param>
    /// <param name="nextAttemptAt">When the next attempt may run, in UTC.</param>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    Task RecordFailureAsync(long intentId, DateTimeOffset now, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);

    /// <summary>Stores everything this poll decided, in one act.</summary>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The durable requests the process dispatches. A poll claims a bounded batch of
/// the requests that are due, skipping the ones another process holds, and returns
/// them in one unit of work; requests are claimed and marked by their own
/// identifier, so several processes can poll the same table safely.
/// </summary>
public interface IIntentStore
{
    /// <summary>Claims the requests that are due, in request order.</summary>
    /// <param name="now">The instant the poll runs, from the process's clock.</param>
    /// <param name="batchSize">The most requests one poll claims.</param>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    Task<IIntentClaim> ClaimDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>How long the oldest request still pending has waited.</summary>
    /// <param name="now">The instant the question is asked, from the process's clock.</param>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    /// <returns>The age, or <see langword="null"/> when no request is pending.</returns>
    Task<TimeSpan?> OldestPendingAgeAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
