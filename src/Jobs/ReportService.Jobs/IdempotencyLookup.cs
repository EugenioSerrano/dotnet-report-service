namespace ReportService.Jobs;

/// <summary>
/// What a request's idempotency key already answers: the key is unused, it is bound
/// to a job the same request created, or it is bound to a job a different request
/// created. The three are exhaustive, so a caller that answers a request from a
/// bound key cannot mistake a repeat for a conflict.
/// </summary>
public abstract record IdempotencyLookup
{
    private IdempotencyLookup()
    {
    }

    /// <summary>The key is not bound to a job of this consumer; the request is a first one.</summary>
    public sealed record None : IdempotencyLookup;

    /// <summary>The key is bound to <paramref name="ReportJobId"/> and the request is the one that created it.</summary>
    /// <param name="ReportJobId">The first job's identifier.</param>
    public sealed record SameRequest(string ReportJobId) : IdempotencyLookup;

    /// <summary>The key is bound to a job of this consumer that a different request created.</summary>
    public sealed record DifferentRequest : IdempotencyLookup;
}
