namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The bounds a job's wait runs under are published to the workflow type once per process,
/// so a case that publishes them and a case that plans the production workers cannot run at
/// the same time: one would see the other's settings. A collection is defined per test
/// assembly, so each assembly whose cases publish those settings defines it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JobWorkflowSettings
{
    /// <summary>The name every case that publishes those settings shares.</summary>
    public const string Name = "the job workflow's published settings";
}
