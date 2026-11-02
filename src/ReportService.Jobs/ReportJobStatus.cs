namespace ReportService.Jobs;

/// <summary>
/// The state of a report job. A job is accepted in <see cref="Queued"/> and moves
/// forward only; the states that end it carry the instant it ended. The record
/// store admits every value of this enumeration so a later step can write its
/// state without a new script.
/// </summary>
public enum ReportJobStatus
{
    /// <summary>Accepted and durable; its execution has not started its work yet.</summary>
    Queued,

    /// <summary>The job's input is being checked against its data profile.</summary>
    Validating,

    /// <summary>The input is being split into the units of work that are printed.</summary>
    Partitioning,

    /// <summary>The job's outputs are being produced.</summary>
    Running,

    /// <summary>Every output is produced and the job is being closed.</summary>
    Finalizing,

    /// <summary>The job ended with every output it asked for.</summary>
    Succeeded,

    /// <summary>The job ended because a step refused it; the code says which condition.</summary>
    Failed,

    /// <summary>A cancellation was requested and the job is being brought to an end.</summary>
    Cancelling,

    /// <summary>The job ended cancelled.</summary>
    Cancelled,
}
