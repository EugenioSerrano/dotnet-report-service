namespace ReportService.Jobs;

/// <summary>
/// The state of one requested output of a job. An accepted job's output is
/// <see cref="Pending"/>; the states after it belong to the steps that produce it.
/// </summary>
public enum OutputStatus
{
    /// <summary>Requested and not started.</summary>
    Pending,

    /// <summary>Being produced.</summary>
    Running,

    /// <summary>Produced and stored.</summary>
    Succeeded,

    /// <summary>Not produced because a step refused it; the code says which condition.</summary>
    Failed,

    /// <summary>Not produced because the job was cancelled.</summary>
    Cancelled,
}
