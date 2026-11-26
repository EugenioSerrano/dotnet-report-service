namespace ReportService.Jobs;

/// <summary>
/// The outcome of a cancel request against one report job, owner-scoped. A request
/// for a non-terminal job that is not already being cancelled transitions it to
/// <see cref="ReportJobStatus.Cancelling"/> and records exactly one durable
/// cancel intent (<see cref="Transitioned"/>); a request while the job is already
/// <see cref="ReportJobStatus.Cancelling"/> records no second intent and does not
/// regress (<see cref="AlreadyCancelling"/>); a request for a job that already
/// reached a terminal state leaves it unchanged (<see cref="AlreadyTerminal"/>);
/// and a job that does not exist for the requesting consumer — absent, or owned by
/// another consumer — is <see cref="NotFound"/> (no existence leak). Every outcome
/// but <see cref="NotFound"/> carries the job's current state for the answer's body.
/// </summary>
public abstract record CancellationRequestResult
{
    private CancellationRequestResult()
    {
    }

    /// <summary>The job moved to <see cref="ReportJobStatus.Cancelling"/> and its cancel intent was recorded.</summary>
    /// <param name="View">The job's state after the transition.</param>
    public sealed record Transitioned(ReportJobStatusView View) : CancellationRequestResult;

    /// <summary>The job was already being cancelled: no second intent, no regress.</summary>
    /// <param name="View">The job's current state.</param>
    public sealed record AlreadyCancelling(ReportJobStatusView View) : CancellationRequestResult;

    /// <summary>The job had already reached a terminal state: an idempotent no-op.</summary>
    /// <param name="View">The job's unchanged terminal state.</param>
    public sealed record AlreadyTerminal(ReportJobStatusView View) : CancellationRequestResult;

    /// <summary>No job with the identifier exists for the requesting consumer.</summary>
    public sealed record NotFound : CancellationRequestResult;
}
