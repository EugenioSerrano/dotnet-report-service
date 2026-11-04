namespace ReportService.Jobs;

/// <summary>
/// One job as its owner reads it: the state, the step it is in, the counters of the
/// work it has done, the totals once they are known and never before, the condition
/// it ended with and the three instants of its life, together with each output it
/// asked for and the file that output published. The value is what one snapshot of
/// the store answered; a consumer never sees a job beside an output from another
/// instant.
/// </summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="Status">The state of the job.</param>
/// <param name="Stage">The step the job is in; null for a terminal job and before any step began.</param>
/// <param name="RecordsRead">How many records of the input the job has read, never fewer than before.</param>
/// <param name="RecordsTotal">How many records the input holds; null until the partition counted them.</param>
/// <param name="BlocksCompleted">How many blocks of the output have been committed.</param>
/// <param name="BlocksTotal">How many blocks the partition holds; null until it was committed.</param>
/// <param name="PagesGenerated">How many pages the committed blocks hold.</param>
/// <param name="ErrorCode">The stable code the job ended with; null unless it failed.</param>
/// <param name="CreatedAt">When the job was accepted, in UTC.</param>
/// <param name="StartedAt">When the job's work began, in UTC; null until then.</param>
/// <param name="FinishedAt">When the job ended, in UTC; null until it did.</param>
/// <param name="Outputs">The outputs the job asked for, in the fixed format order.</param>
public sealed record ReportJobStatusView(
    string ReportJobId,
    ReportJobStatus Status,
    ReportJobStage? Stage,
    long RecordsRead,
    long? RecordsTotal,
    int BlocksCompleted,
    int? BlocksTotal,
    long PagesGenerated,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<ReportOutputStatusView> Outputs);
