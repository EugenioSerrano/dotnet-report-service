namespace ReportService.Jobs;

/// <summary>
/// What accepting a job came to: the job as it was committed, or the fact that the
/// upload it was asked for is not usable. The two are exhaustive — an acceptance
/// either stored the whole job or stored nothing at all — so a caller cannot read
/// a half-written job by mistake.
/// </summary>
public abstract record JobAcceptanceResult
{
    private JobAcceptanceResult()
    {
    }

    /// <summary>The job, its outputs, its template reference and its start request, committed together.</summary>
    /// <param name="Job">The job as it was stored.</param>
    public sealed record Accepted(ReportJobRecord Job) : JobAcceptanceResult;

    /// <summary>
    /// The upload named by the acceptance is not usable — absent, owned by another
    /// consumer, not confirmed, refused, expired or overdue — and nothing was stored.
    /// </summary>
    public sealed record UploadUnusable : JobAcceptanceResult;

    /// <summary>
    /// The acceptance's key is already bound to <paramref name="ReportJobId"/> by the same
    /// request, so nothing was stored and the first job is the answer.
    /// </summary>
    /// <param name="ReportJobId">The first job's identifier.</param>
    public sealed record SameRequest(string ReportJobId) : JobAcceptanceResult;

    /// <summary>
    /// The acceptance's key is already bound to a job of this consumer by a different
    /// request, so nothing was stored and the first job stays exactly as it is.
    /// </summary>
    public sealed record DifferentRequest : JobAcceptanceResult;
}
