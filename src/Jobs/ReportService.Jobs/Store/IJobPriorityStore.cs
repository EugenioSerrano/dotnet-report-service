namespace ReportService.Jobs;

/// <summary>
/// The class a job is dispatched on, as the orchestration reads and moves it: read once when
/// the job's execution starts dispatching, and moved from <see cref="JobPriority.Low"/> to
/// <see cref="JobPriority.Normal"/> once, by the aging promotion. The declared priority is never
/// touched.
/// </summary>
public interface IJobPriorityStore
{
    /// <summary>The class the job is dispatched on, or none when no such job exists.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<JobPriority?> ReadEffectiveClassAsync(string reportJobId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the job's effective class from low to normal, and answers whether this call moved
    /// it: a job that is already normal or high, or that does not exist, is left as it is and the
    /// answer is <see langword="false"/>. A repeated call therefore moves it once.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<bool> PromoteEffectiveClassAsync(string reportJobId, CancellationToken cancellationToken);
}
