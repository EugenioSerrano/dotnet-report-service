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
}
