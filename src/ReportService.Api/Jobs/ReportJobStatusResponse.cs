using ReportService.Jobs;
using ReportService.Uploads;

namespace ReportService.Api.Jobs;

/// <summary>
/// What a job's owner reads: the state, the step the job is in, the counters of the
/// work it has done, each requested output with its own state and the identifier of
/// the file it published, the condition it failed with and the three instants of its
/// life. Every member is always present and is null while it does not apply, so a
/// consumer branches on null and never on a member that is missing. There is no
/// percentage at any time: a total a consumer may compute from is only ever the count
/// the data step fixed, and the answer never presents a guessed one.
/// </summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="Status">The state of the job, in its answer text.</param>
/// <param name="Stage">The step the job is in, in its answer text; null for a terminal job and before any step began.</param>
/// <param name="Progress">The counters of the job's work.</param>
/// <param name="Outputs">The outputs the job asked for, in the fixed format order.</param>
/// <param name="ErrorCode">The stable code the job failed with; null unless it failed.</param>
/// <param name="CreatedAt">When the job was accepted, in UTC.</param>
/// <param name="StartedAt">When the job's work began, in UTC; null until then.</param>
/// <param name="FinishedAt">When the job ended, in UTC; null until it did.</param>
public sealed record ReportJobStatusResponse(
    string ReportJobId,
    string Status,
    string? Stage,
    JobProgressResponse Progress,
    IReadOnlyList<ReportOutputStatusResponse> Outputs,
    string? ErrorCode,
    string? CreatedAt,
    string? StartedAt,
    string? FinishedAt)
{
    /// <summary>Projects a stored job onto the answer.</summary>
    /// <param name="view">The job as the store read it.</param>
    public static ReportJobStatusResponse From(ReportJobStatusView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new ReportJobStatusResponse(
            view.ReportJobId,
            JobWireValues.ToWire(view.Status),
            view.Stage is { } stage ? JobWireValues.ToWire(stage) : null,
            new JobProgressResponse(
                view.RecordsRead,
                view.RecordsTotal,
                view.BlocksCompleted,
                view.BlocksTotal,
                view.PagesGenerated),
            [.. view.Outputs.Select(ReportOutputStatusResponse.From)],
            view.ErrorCode,
            UploadWireValues.FormatTimestamp(view.CreatedAt),
            view.StartedAt is { } started ? UploadWireValues.FormatTimestamp(started) : null,
            view.FinishedAt is { } finished ? UploadWireValues.FormatTimestamp(finished) : null);
    }
}

/// <summary>The counters of a job's work: what it has read, what it has produced and the totals once they are known.</summary>
/// <param name="RecordsRead">How many records of the input the job has read.</param>
/// <param name="RecordsTotal">How many records the input holds; null until the partition counted them.</param>
/// <param name="BlocksCompleted">How many blocks of the output were committed.</param>
/// <param name="BlocksTotal">How many blocks the partition holds; null until it was committed.</param>
/// <param name="PagesGenerated">How many pages the committed blocks hold.</param>
public sealed record JobProgressResponse(
    long RecordsRead,
    long? RecordsTotal,
    int BlocksCompleted,
    int? BlocksTotal,
    long PagesGenerated);

/// <summary>One output of a job, as its owner reads it.</summary>
/// <param name="Format">The form the output is produced in, in its answer text.</param>
/// <param name="Status">The state of the output, in its answer text.</param>
/// <param name="ArtifactId">The identifier of the published file; null unless the output succeeded.</param>
/// <param name="ErrorCode">The stable code the output failed with; null unless it failed.</param>
public sealed record ReportOutputStatusResponse(
    string Format,
    string Status,
    string? ArtifactId,
    string? ErrorCode)
{
    /// <summary>Projects one stored output onto the answer.</summary>
    /// <param name="view">The output as the store read it.</param>
    public static ReportOutputStatusResponse From(ReportOutputStatusView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new ReportOutputStatusResponse(
            JobWireValues.ToWire(view.Format),
            JobWireValues.ToWire(view.Status),
            view.ArtifactId,
            view.ErrorCode);
    }
}
