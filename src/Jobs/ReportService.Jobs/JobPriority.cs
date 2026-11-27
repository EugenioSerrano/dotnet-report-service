namespace ReportService.Jobs;

/// <summary>
/// The priority a consumer declares for a job, and the class a job is dispatched on. There
/// are exactly three, and <see cref="Normal"/> is what a job that declares none receives. The
/// declared priority is fixed when the job is accepted; the class a job is dispatched on is a
/// separate fact of the job, so a later scheduling rule can move it without rewriting what the
/// consumer asked for.
/// </summary>
public enum JobPriority
{
    /// <summary>Work that may wait behind the other classes.</summary>
    Low,

    /// <summary>The default class: what a job that declares no priority receives.</summary>
    Normal,

    /// <summary>Work that goes ahead of queued work of the other classes.</summary>
    High,
}
