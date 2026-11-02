namespace ReportService.Jobs;

/// <summary>
/// The report job records of the service. Accepting a job is one act: the upload
/// it reports on is looked at under a lock that keeps it true until the job
/// commits, and the job, its output, its template reference and its start request
/// are written together or not at all. Failing a job is one act too, and it ends
/// every output of the job that had not ended yet. Every write is conditional, so
/// a step that arrives twice or too late changes nothing.
/// </summary>
public interface IJobStore
{
    /// <summary>
    /// Accepts a job, in one transaction.
    /// </summary>
    /// <param name="acceptance">The job to record, with the identifiers it will carry.</param>
    /// <param name="now">The instant the acceptance is decided at, in UTC; the job's clock and its creation are this instant.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>
    /// The job as committed, or <see cref="JobAcceptanceResult.UploadUnusable"/> when the
    /// upload is absent for this consumer, not confirmed yet, refused, expired or overdue —
    /// in which case nothing was written.
    /// </returns>
    Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a job, its outputs and the state of the upload it reports on, by the
    /// job's identifier alone. This read exists for the job's own steps and takes no
    /// caller text: the identifier comes from the durable request that started them.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>The job with its outputs, or <see langword="null"/> when no such job exists.</returns>
    Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken);

    /// <summary>
    /// Fails a job and every output of it that has not ended, in one transaction: the
    /// outputs are marked failed with <paramref name="code"/> and the job is marked
    /// failed with the code and the reason. A job that already ended is left exactly
    /// as it is, and the call says so.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="code">The code of the condition that ended the job.</param>
    /// <param name="reason">The fixed reason recorded beside the code.</param>
    /// <param name="now">The instant the failure is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, DateTimeOffset now, CancellationToken cancellationToken);
}
