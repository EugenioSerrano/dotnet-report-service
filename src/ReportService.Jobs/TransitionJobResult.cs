namespace ReportService.Jobs;

/// <summary>
/// What moving a job to another state came to. A state the job was moved to answers
/// the instant the job was accepted, which is what the step that measures the time
/// from acceptance to the first stage reads; a repeated move or one that does not
/// apply wrote nothing and carries no instant.
/// </summary>
/// <param name="Outcome">Whether this call moved the job, found it there already, or could not move it.</param>
/// <param name="CreatedAt">When the job was accepted, in UTC; null when nothing was written.</param>
public sealed record TransitionJobResult(TransitionOutcome Outcome, DateTimeOffset? CreatedAt);

/// <summary>
/// What a progress write came to: the stored counter was raised to the value the
/// write carried, or the write changed nothing — because the value was not higher,
/// was above a total already known, or the job had ended.
/// </summary>
public enum ProgressWriteOutcome
{
    /// <summary>The stored counter was raised.</summary>
    Raised,

    /// <summary>Nothing was written; the stored counter stays as it was.</summary>
    Unchanged,
}
