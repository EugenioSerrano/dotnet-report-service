using ReportService.Jobs;

namespace ReportService.Testing;

/// <summary>
/// The cancel-request operation of the in-memory store, under the same lock and with
/// the same owner-scoped, idempotent semantics as the database: a non-terminal,
/// not-already-cancelling job of the consumer moves to <see cref="ReportJobStatus.Cancelling"/>
/// and records one durable cancel intent; a repeat while cancelling records no second
/// intent; a terminal job is left unchanged; a job of another consumer, or an absent
/// one, is the same not-found. Because the shared contract runs against both this class
/// and the real store, the double cannot drift away from what it stands in for.
/// </summary>
public sealed partial class InMemoryJobStore
{
    /// <summary>The durable-intent kind a cancel request records, exactly as the database stores it.</summary>
    public const string CancelJobKind = "cancel-job";

    /// <inheritdoc />
    public async Task<CancellationRequestResult> RequestCancellationAsync(
        string reportJobId,
        string consumerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(consumerId);

        CancelOutcome outcome;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job)
                || !string.Equals(job.ConsumerId, consumerId, StringComparison.Ordinal))
            {
                // A job of another consumer is answered the same as a job that does not exist:
                // no existence leak, and nothing is written.
                outcome = CancelOutcome.NotFound;
            }
            else if (job.IsTerminal)
            {
                outcome = CancelOutcome.AlreadyTerminal;
            }
            else if (job.Status == ReportJobStatus.Cancelling)
            {
                outcome = CancelOutcome.AlreadyCancelling;
            }
            else
            {
                // The transition and the single durable cancel intent are one atomic act, as
                // the database commits them in one transaction.
                Interlocked.Increment(ref _writeCalls);
                _jobs[reportJobId] = job with { Status = ReportJobStatus.Cancelling };
                _intents.Add(new RecordedJobIntent(CancelJobKind, reportJobId, now.ToUniversalTime()));
                outcome = CancelOutcome.Transitioned;
            }
        }

        if (outcome == CancelOutcome.NotFound)
        {
            return new CancellationRequestResult.NotFound();
        }

        // The job exists for this consumer, so the owner-scoped read never answers null; it
        // carries the current state for the answer's body.
        var view = (await FindStatusAsync(reportJobId, consumerId, cancellationToken).ConfigureAwait(false))!;
        return outcome switch
        {
            CancelOutcome.Transitioned => new CancellationRequestResult.Transitioned(view),
            CancelOutcome.AlreadyCancelling => new CancellationRequestResult.AlreadyCancelling(view),
            _ => new CancellationRequestResult.AlreadyTerminal(view),
        };
    }

    /// <summary>
    /// Puts a job directly into a non-terminal status a case needs but no stage-bearing
    /// transition of this increment reaches (<see cref="ReportJobStatus.Partitioning"/>,
    /// <see cref="ReportJobStatus.Finalizing"/>), so the cancel guard can be shown to admit it.
    /// </summary>
    /// <param name="reportJobId">The job to place.</param>
    /// <param name="status">The non-terminal status to place it in.</param>
    public void PlaceInStatus(string reportJobId, ReportJobStatus status)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        lock (_gate)
        {
            if (_jobs.TryGetValue(reportJobId, out var job))
            {
                _jobs[reportJobId] = job with { Status = status };
            }
        }
    }

    // The branch a cancel request took under the lock, classified before the view is read.
    private enum CancelOutcome
    {
        NotFound,
        Transitioned,
        AlreadyCancelling,
        AlreadyTerminal,
    }
}
