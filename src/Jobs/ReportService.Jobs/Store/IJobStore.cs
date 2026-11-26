using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;

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
    /// The job as committed; or <see cref="JobAcceptanceResult.UploadUnusable"/> when the
    /// upload is absent for this consumer, not confirmed yet, refused, expired or overdue;
    /// or <see cref="JobAcceptanceResult.SameRequest"/> / <see cref="JobAcceptanceResult.DifferentRequest"/>
    /// when the acceptance's key is already bound — in every one of those cases nothing was
    /// written. The key is decided inside the transaction, by the unique index, before the
    /// upload is looked at, so a request that races another with the same key always receives
    /// the winner's job rather than a refusal about an upload it would no longer see.
    /// </returns>
    Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a job by the idempotency key a request bound to it, for the consumer that sent
    /// that request, and compares the key's stored fingerprint with
    /// <paramref name="fingerprint"/>. A bound row never changes, so this is a plain read
    /// that takes no lock; a key of another consumer is not found, and nothing reveals that
    /// it exists.
    /// </summary>
    /// <param name="consumerId">The declared consumer that owns the key.</param>
    /// <param name="idempotencyKey">The key exactly as the request sent it.</param>
    /// <param name="fingerprint">The identity of the request being answered.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The key's answer: unused, the same request, or a different one.</returns>
    Task<IdempotencyLookup> FindByIdempotencyKeyAsync(
        string consumerId,
        string idempotencyKey,
        RequestFingerprint fingerprint,
        CancellationToken cancellationToken);

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
    /// failed with the code, the reason and the message. A job that already ended is left
    /// exactly as it is, and the call says so. A job that is being cancelled is left exactly
    /// as it is too: the cancellation's finalisation owns its closure and writes CANCELLED
    /// (ADR-048 R0), so a failure racing the cancellation — a rejected input, an expired
    /// upload, a refused template — writes nothing.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="code">The code of the condition that ended the job.</param>
    /// <param name="reason">The fixed reason recorded beside the code.</param>
    /// <param name="message">The message the job carries, or <see langword="null"/> for none.</param>
    /// <param name="now">The instant the failure is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, string? message, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Fails a job with no message. The delivered callers end a job from a condition the
    /// service raises itself, and the message is the template step's own addition.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="code">The code of the condition that ended the job.</param>
    /// <param name="reason">The fixed reason recorded beside the code.</param>
    /// <param name="now">The instant the failure is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        FailJobAsync(reportJobId, code, reason, null, now, cancellationToken);

    /// <summary>
    /// Writes the version a job's template is fixed to, once, and fixes the presentation the
    /// job's report is written in with the same write: a culture or time zone the acceptance
    /// left open is completed from the presentation the taken package declares, and a value
    /// the request named is never replaced. The write is conditional on the job not having
    /// ended and on no pin being recorded yet, so the first committed pin wins: an attempt
    /// that arrives second receives the pin that is already there, whatever it computed
    /// itself, and completes nothing.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="pin">The values the job is fixed to.</param>
    /// <param name="now">The instant the pin is written at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <param name="presentation">
    /// The culture and time zone the taken package declares, written to a job member the
    /// request left open; <see langword="null"/> when the package declares none.
    /// </param>
    /// <returns>The pin the record holds, or that the job had already ended.</returns>
    Task<TemplatePinState> PinTemplateAsync(
        string reportJobId,
        TemplatePin pin,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        TemplateLocaleDefaults? presentation);

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
    /// when it first started and the step the target state enters. A job already carrying
    /// <paramref name="to"/> answers <see cref="TransitionOutcome.AlreadyApplied"/>; one in
    /// any other state is left exactly as it is.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="from">The states the change may start from.</param>
    /// <param name="to">The state to move it to.</param>
    /// <param name="now">The instant the change is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<TransitionJobResult> TransitionJobAsync(
        string reportJobId,
        IReadOnlyList<ReportJobStatus> from,
        ReportJobStatus to,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Requests the cancellation of one report job, owner-scoped, in one transaction: a
    /// job of <paramref name="consumerId"/> that is non-terminal and not already being
    /// cancelled is moved to <see cref="ReportJobStatus.Cancelling"/> and a single durable
    /// <c>cancel-job</c> intent is recorded for it, together; a job already
    /// <see cref="ReportJobStatus.Cancelling"/> records no second intent and does not
    /// regress; a job already in a terminal state is left exactly as it is; and a job that
    /// does not exist for the consumer — absent, or owned by another consumer — is
    /// <see cref="CancellationRequestResult.NotFound"/>, the same answer either way, so no
    /// other consumer's job is revealed. Every write is conditional on the job's current
    /// status, so a job that reached a terminal state between a read and this call is not
    /// moved (the cancel/completion race: whichever commits first wins).
    /// </summary>
    /// <param name="reportJobId">The identifier of the job to cancel.</param>
    /// <param name="consumerId">The declared consumer that must own the job.</param>
    /// <param name="now">The instant the cancel intent is recorded at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <remarks>
    /// A default implementation is provided so the narrow test doubles of unrelated features —
    /// which never exercise cancellation — need not implement it; the two real stores
    /// (<c>PostgresJobStore</c>, <c>InMemoryJobStore</c>) and the cancel surface's own doubles
    /// override it. Calling it on a store that did not is a programming error, not a job outcome.
    /// </remarks>
    Task<CancellationRequestResult> RequestCancellationAsync(
        string reportJobId,
        string consumerId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("this job store does not implement cancellation requests");

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
    /// Reads every part commit of an output, ordered by block index: the ordered
    /// input the assembly reads, independent of the order the renders finished in.
    /// </summary>
    /// <param name="outputId">The identifier of the output.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>The commits, ordered by block index; empty when none exists.</returns>
    /// <remarks>
    /// A default implementation is provided so the narrow test doubles of unrelated features —
    /// which never assemble an output — need not implement it; the two real stores
    /// (<c>PostgresJobStore</c>, <c>InMemoryJobStore</c>) override it. Calling it on a store that
    /// did not is a programming error, not a job outcome.
    /// </remarks>
    Task<IReadOnlyList<PartCommit>> ListPartsAsync(string outputId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("this job store does not implement the ordered part read");

    /// <summary>
    /// Publishes an output's artifact in one transaction under the same guard as a block
    /// commit, and marks the output succeeded. An output already published is reported with
    /// the artifact that exists.
    /// </summary>
    /// <param name="artifact">The artifact to record.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one job's published artifacts as its owner reads them, in the fixed format
    /// order. The read is scoped by the consumer that owns the job, so a job of another
    /// consumer answers the same as a job that does not exist.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="consumerId">The consumer that must own the job.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The job's artifacts, or <see langword="null"/> when no job of that consumer has the identifier.</returns>
    Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one artifact of one job as its owner reads it. An absent job, a job of another
    /// consumer, an absent artifact and an artifact of another job answer the same: nothing.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="artifactId">The identifier of the artifact.</param>
    /// <param name="consumerId">The consumer that must own the job.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The artifact, or <see langword="null"/> when that owner has no such artifact.</returns>
    Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken);

    /// <summary>Ends one output that has not ended yet with the code of the condition.</summary>
    /// <param name="outputId">The identifier of the output.</param>
    /// <param name="code">The code of the condition that ended it.</param>
    /// <param name="reason">The diagnostic recorded beside the code, or <see langword="null"/> when the condition carries none.</param>
    /// <param name="now">The instant the failure is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <remarks>
    /// After the job was cancelled no output is failed: the cancellation rollup writes the
    /// remaining outputs <c>CANCELLED</c> (US-010 AC-4). The call answers
    /// <see cref="OutputFailureOutcome.JobCancelling"/> and writes nothing when the output's job
    /// is <c>CANCELLING</c>/<c>CANCELLED</c>, distinct from an output that had already ended.
    /// </remarks>
    Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, string? reason, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Closes a job from the states its outputs imply, in one transaction: every output
    /// succeeded makes the job succeeded, a failed output makes it failed with that output's
    /// code and - when it carries one - its own diagnostic; an output that carries none keeps
    /// the fixed reason of an output-scoped failure. A job that already ended, or whose outputs
    /// have not all ended, is left as it is. A job that is being cancelled is closed
    /// <c>CANCELLED</c> by ADR-048 <b>R0</b>: its outputs still <c>PENDING</c>/<c>RUNNING</c> are
    /// written <c>CANCELLED</c> in the same transaction, an output that already succeeded keeps
    /// its status and its published artifact, and the "every output terminal" wait does not hold
    /// a cancelled job.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="now">The instant the close is decided at, in UTC.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records the deadline class one rendering attempt ended with, once per output, block
    /// and attempt. The first row for an attempt wins: a repeat, from a race or from a step
    /// that arrives twice, writes nothing and answers that the row exists already.
    /// </summary>
    /// <param name="cause">The attempt and the deadline class it ended with.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>Whether this call wrote the row.</returns>
    Task<AttemptCauseOutcome> RecordAttemptCauseAsync(AttemptCause cause, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the last cause recorded for one block of an output - the row with the greatest
    /// attempt - or <see langword="null"/> when no attempt recorded one. A crash records
    /// nothing, so "the last recorded" is the most recent attempt that could write.
    /// </summary>
    /// <param name="outputId">The identifier of the output.</param>
    /// <param name="blockIndex">The block's position in the output's partition.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>The last recorded cause, or <see langword="null"/> when there is none.</returns>
    Task<AttemptCause?> FindLastAttemptCauseAsync(string outputId, int blockIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one job as its owner reads it, in one snapshot: the job, its outputs in the
    /// fixed format order and the identifier of each published output. The read is scoped
    /// by the consumer that owns the job, so a job of another consumer answers the same as
    /// a job that does not exist.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="consumerId">The consumer that must own the job.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The job as it stands, or <see langword="null"/> when no job of that consumer has the identifier.</returns>
    Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken);

    /// <summary>
    /// Records how many records of its input a job has read, raising the stored counter
    /// and never lowering it. A value that is not higher, one above a total already known,
    /// and any write after the job ended change nothing.
    /// </summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    /// <param name="recordsRead">How many records the job's step has read so far.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken);
}
