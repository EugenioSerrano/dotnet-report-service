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
    /// Reads the template reference the job was accepted with: the template it asked
    /// for, the form of the reference and, when it named one, the version. The reference
    /// is written with the job, so a job that exists has one, and a job that is not
    /// recorded answers <see langword="null"/>.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>The reference, or <see langword="null"/> when no such job exists.</returns>
    Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken);

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

    /// <summary>
    /// Records that a queued job is waiting for its upload to be verified. The write is
    /// conditional on the job still being queued, sets the stage and nothing else, and
    /// answers the same on a repeat: a job that ended meanwhile is left exactly as it is.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the waiting stage of a job, conditionally on its still carrying it. The write
    /// sets the stage and nothing else; a repeated clear changes nothing.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the jobs of an upload that may be waiting for it, in the order they were
    /// accepted: the queued ones that carry the waiting stage, and the queued ones that
    /// carry no stage yet — the race before a wait begins and the step between two stages.
    /// </summary>
    /// <param name="uploadId">The identifier of the upload.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a job from one of <paramref name="from"/> to <paramref name="to"/>, recording
    /// when it first started. A job already carrying <paramref name="to"/> answers
    /// <see cref="TransitionOutcome.AlreadyApplied"/>; one in any other state is left exactly
    /// as it is.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="from">The states the change may start from.</param>
    /// <param name="to">The state to move it to.</param>
    /// <param name="now">The instant the change is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<TransitionOutcome> TransitionJobAsync(
        string reportJobId,
        IReadOnlyList<ReportJobStatus> from,
        ReportJobStatus to,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Moves a pending output to running, recording when it started.</summary>
    /// <param name="outputId">The identifier of the output.</param>
    /// <param name="now">The instant the change is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records a job's partition once. A partition another attempt wrote first is reported
    /// with the record it wrote, never replaced.
    /// </summary>
    /// <param name="partition">The partition to record.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken);

    /// <summary>Reads the partition of a job, or <see langword="null"/> when none was recorded.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken);

    /// <summary>
    /// Commits one rendered block in a transaction that holds the guard: the job is not
    /// ending or ended, and the output is still running. A commit another attempt wrote first
    /// is reported with the record it wrote, never replaced.
    /// </summary>
    /// <param name="commit">The commit to record.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken);

    /// <summary>Reads the commit of one block of an output, or <see langword="null"/> when none exists.</summary>
    /// <param name="outputId">The identifier of the output.</param>
    /// <param name="blockIndex">The block's position in the partition.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes an output's artifact in one transaction under the same guard as a block
    /// commit, and marks the output succeeded. An output already published is reported with
    /// the artifact that exists.
    /// </summary>
    /// <param name="artifact">The artifact to record.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken);

    /// <summary>Ends one output that has not ended yet with the code of the condition.</summary>
    /// <param name="outputId">The identifier of the output.</param>
    /// <param name="code">The code of the condition that ended it.</param>
    /// <param name="now">The instant the failure is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Closes a job from the states its outputs imply, in one transaction: every output
    /// succeeded makes the job succeeded, a failed output makes it failed with that output's
    /// code. A job that already ended, or whose outputs have not all ended, is left as it is.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="now">The instant the close is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken);
}
