using ReportService.Jobs;

namespace ReportService.Api.Jobs;

/// <summary>
/// What creating a job answers: the identifier of the job, its state in the
/// answer's own words and the address at which it can be followed. The three
/// members are always present, and the address is the one the answer's
/// <c>Location</c> header carries.
/// </summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="Status">The state of the job, in its answer text.</param>
/// <param name="StatusUrl">Where the job can be followed.</param>
public sealed record ReportJobAcceptedResponse(string ReportJobId, string Status, string StatusUrl)
{
    /// <summary>Projects an accepted job onto the answer.</summary>
    /// <param name="job">The job as it was committed.</param>
    /// <param name="statusUrl">Where the job can be followed.</param>
    public static ReportJobAcceptedResponse From(ReportJobRecord job, string statusUrl)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(statusUrl);
        return new ReportJobAcceptedResponse(job.ReportJobId, JobWireValues.ToWire(job.Status), statusUrl);
    }
}
