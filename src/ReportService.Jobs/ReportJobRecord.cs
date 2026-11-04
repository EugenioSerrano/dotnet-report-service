namespace ReportService.Jobs;

/// <summary>
/// One report job as the service records it: the identifier the consumer
/// receives, its owner, the upload whose data it reports on, its state and the
/// step it is in, the instant its report clock was fixed at acceptance, the
/// condition it ended with when it failed, and the three instants of its life.
/// The value is immutable; a later state change replaces it. A job never moves
/// backwards and never changes after it ended.
/// </summary>
/// <param name="ReportJobId">The identifier of the job, unique across the service.</param>
/// <param name="ConsumerId">The declared consumer that owns the job.</param>
/// <param name="UploadId">The upload whose data the job reports on.</param>
/// <param name="Status">The state of the job.</param>
/// <param name="ReportClock">The instant every date and time of the report is read as, fixed at acceptance.</param>
/// <param name="CreatedAt">When the job was accepted, in UTC.</param>
public sealed record ReportJobRecord(
    string ReportJobId,
    string ConsumerId,
    string UploadId,
    ReportJobStatus Status,
    DateTimeOffset ReportClock,
    DateTimeOffset CreatedAt)
{
    /// <summary>The step the job is in; null while no step has begun.</summary>
    public string? Stage { get; init; }

    /// <summary>Why the job ended failed; null unless it failed.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The fixed reason recorded beside <see cref="ErrorCode"/>.</summary>
    public string? ErrorReason { get; init; }

    /// <summary>The message the job carries for a consumer: the failing step's own text; null when it carried none.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the job's work began, in UTC; null until then.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the job ended, in UTC; null until it did.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Whether the job has ended and can no longer change.</summary>
    public bool IsTerminal => Status is ReportJobStatus.Succeeded or ReportJobStatus.Failed or ReportJobStatus.Cancelled;
}
