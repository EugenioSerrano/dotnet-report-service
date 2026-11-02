namespace ReportService.Jobs;

/// <summary>
/// One output a job asked for, as the service records it: its identifier, the job
/// it belongs to, the form it is produced in, its state and, once it ended, the
/// instant it did and — when it failed — the condition it failed with. One job
/// asks for a format once.
/// </summary>
/// <param name="OutputId">The identifier of the output, unique across the service.</param>
/// <param name="ReportJobId">The job that asked for it.</param>
/// <param name="Format">The form the output is produced in.</param>
/// <param name="Status">The state of the output.</param>
public sealed record ReportOutputRecord(
    string OutputId,
    string ReportJobId,
    OutputFormat Format,
    OutputStatus Status)
{
    /// <summary>Why the output failed; null unless it failed.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>When the output's production began, in UTC; null until then.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the output ended, in UTC; null until it did.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
}
