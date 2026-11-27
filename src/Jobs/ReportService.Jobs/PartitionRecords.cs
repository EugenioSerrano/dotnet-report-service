namespace ReportService.Jobs;

/// <summary>
/// The partition of a job: the block and the index page the data step wrote, with
/// the interval and the content they hold. It is written once per job, so a step
/// that arrives twice reads the record that exists instead of writing another.
/// </summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="PartitionerVersion">The version of the partitioning rules used.</param>
/// <param name="BlockCount">How many blocks the partition holds.</param>
/// <param name="RecordCount">How many records the partition holds.</param>
/// <param name="RecordBytes">How many bytes of record content the partition holds.</param>
/// <param name="IndexObjectKey">The key the index page was written under.</param>
/// <param name="IndexSha256">The SHA-256 of the index page, lower-case hexadecimal.</param>
/// <param name="IndexSizeBytes">The size of the index page.</param>
/// <param name="CommittedAt">When the partition became durable, in UTC.</param>
/// <param name="PdfRefusalCode">The code the PDF output was refused with, when the pass refused it.</param>
/// <param name="PdfRefusalReason">The stored reason of that refusal, kept with the partition so a repeated pass answers it.</param>
public sealed record PartitionRecord(
    string ReportJobId,
    int PartitionerVersion,
    int BlockCount,
    long RecordCount,
    long RecordBytes,
    string IndexObjectKey,
    string IndexSha256,
    long IndexSizeBytes,
    DateTimeOffset CommittedAt,
    string? PdfRefusalCode = null,
    string? PdfRefusalReason = null);

/// <summary>One rendered block of an output, committed once under a write-once key.</summary>
/// <param name="OutputId">The identifier of the output the block belongs to.</param>
/// <param name="BlockIndex">The block's position in the partition, from 0.</param>
/// <param name="ObjectKey">The key the rendered block was written under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
/// <param name="PageCount">How many pages the block's document holds.</param>
/// <param name="TemplatePackageSha256">The SHA-256 of the template package the block was printed from.</param>
/// <param name="CommittedAt">When the commit became durable, in UTC.</param>
/// <param name="Presentation">What produced the block's presentation, or <see langword="null"/> when the row carries the value of a block written without one.</param>
public sealed record PartCommit(
    string OutputId,
    int BlockIndex,
    string ObjectKey,
    string Sha256,
    long SizeBytes,
    int PageCount,
    string TemplatePackageSha256,
    DateTimeOffset CommittedAt,
    PresentationProvenance? Presentation = null);

/// <summary>One published artifact: the output-class object a consumer can be told about.</summary>
/// <param name="ArtifactId">The identifier of the artifact.</param>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the output the artifact is the result of.</param>
/// <param name="Format">The format of the artifact.</param>
/// <param name="MediaType">The media type the artifact is served as.</param>
/// <param name="ObjectKey">The output-class key the artifact is stored under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
/// <param name="PageCount">How many pages the artifact holds, when the format has pages.</param>
/// <param name="CreatedAt">When the artifact became durable, in UTC.</param>
public sealed record ArtifactRecord(
    string ArtifactId,
    string ReportJobId,
    string OutputId,
    OutputFormat Format,
    string MediaType,
    string ObjectKey,
    string Sha256,
    long SizeBytes,
    int? PageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>One published artifact as its owner's list and download read it.</summary>
/// <param name="ArtifactId">The identifier of the artifact.</param>
/// <param name="ReportJobId">The job the artifact was published for.</param>
/// <param name="Format">The format the artifact carries.</param>
/// <param name="MediaType">The media type stored with it.</param>
/// <param name="ObjectKey">The key the bytes are stored under.</param>
/// <param name="Sha256">The SHA-256 of the stored bytes, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
/// <param name="CreatedAt">When the artifact was published, in UTC.</param>
/// <param name="ExpiresAt">When it stops being downloadable, in UTC.</param>
public sealed record ArtifactView(
    string ArtifactId,
    string ReportJobId,
    OutputFormat Format,
    string MediaType,
    string ObjectKey,
    string Sha256,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>One job's published artifacts, in the fixed format order.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="Artifacts">Its artifacts, empty when it has published none.</param>
public sealed record JobArtifactsView(string ReportJobId, IReadOnlyList<ArtifactView> Artifacts);

/// <summary>What a conditional status change did.</summary>
public enum TransitionOutcome
{
    /// <summary>The row was in one of the expected states and now carries the target.</summary>
    Applied,

    /// <summary>The row already carried the target; nothing was written.</summary>
    AlreadyApplied,

    /// <summary>The row was in a state the change does not start from; nothing was written.</summary>
    NotApplicable,
}

/// <summary>What the commit of a partition did.</summary>
public enum PartitionCommitOutcome
{
    /// <summary>This call wrote the partition.</summary>
    Committed,

    /// <summary>Another attempt wrote the partition first; the record it wrote is returned.</summary>
    AlreadyCommitted,
}

/// <summary>The result of committing a partition.</summary>
/// <param name="Outcome">Whether this call wrote it.</param>
/// <param name="Existing">The partition as it stands, which is the one this call wrote or the one it found.</param>
public sealed record PartitionCommitResult(PartitionCommitOutcome Outcome, PartitionRecord Existing);

/// <summary>What the commit of a rendered block did.</summary>
public enum PartCommitOutcome
{
    /// <summary>This call wrote the commit.</summary>
    Committed,

    /// <summary>Another attempt committed this block first; the commit it wrote is returned.</summary>
    AlreadyCommitted,

    /// <summary>The job is ending or has ended, or the output is no longer running; nothing was written.</summary>
    GuardRefused,

    /// <summary>The commit would pass the configured document capacity; it was rolled back and the output failed.</summary>
    LimitExceeded,
}

/// <summary>The result of committing a rendered block.</summary>
/// <param name="Outcome">Whether this call wrote it, found it, or was refused.</param>
/// <param name="Existing">The commit as it stands, when one exists.</param>
public sealed record PartCommitResult(PartCommitOutcome Outcome, PartCommit? Existing)
{
    /// <summary>The limit that was passed, when the outcome is <see cref="PartCommitOutcome.LimitExceeded"/>.</summary>
    public string? Limit { get; init; }

    /// <summary>The limit's value, when the outcome is <see cref="PartCommitOutcome.LimitExceeded"/>.</summary>
    public long LimitValue { get; init; }

    /// <summary>The value the commit would reach, when the outcome is <see cref="PartCommitOutcome.LimitExceeded"/>.</summary>
    public long ReachedValue { get; init; }
}

/// <summary>What the publication of an output did.</summary>
public enum PublishOutcome
{
    /// <summary>This call published the output's artifact.</summary>
    Published,

    /// <summary>The output was published already; the artifact that exists is named.</summary>
    AlreadyPublished,

    /// <summary>The job is ending or has ended, or the output is no longer running; nothing was written.</summary>
    GuardRefused,
}

/// <summary>The result of publishing an output.</summary>
/// <param name="Outcome">Whether this call published it, found it, or was refused.</param>
/// <param name="ArtifactId">The identifier of the artifact that exists.</param>
public sealed record PublishResult(PublishOutcome Outcome, string? ArtifactId);

/// <summary>What ending one output did.</summary>
public enum OutputFailureOutcome
{
    /// <summary>The output had not ended and now carries the code.</summary>
    Applied,

    /// <summary>The output had already ended; nothing was written.</summary>
    AlreadyTerminal,

    /// <summary>
    /// The job is being cancelled (or was cancelled); nothing was written and the output stays
    /// for the cancellation rollup. Distinct from <see cref="AlreadyTerminal"/>: an output that
    /// had not ended is not ended by a failure once its job was cancelled (ADR-048 R0).
    /// </summary>
    JobCancelling,
}

/// <summary>What closing a job did.</summary>
public enum JobFinalizationOutcome
{
    /// <summary>The job's outputs all ended, and the job now carries the status they imply.</summary>
    Applied,

    /// <summary>The job had already ended, or its outputs have not all ended; nothing was written.</summary>
    AlreadyTerminal,
}

/// <summary>The result of closing a job.</summary>
/// <param name="Outcome">Whether this call ended the job.</param>
/// <param name="Status">The status the job carries.</param>
public sealed record JobFinalizationResult(JobFinalizationOutcome Outcome, ReportJobStatus? Status)
{
    /// <summary>The code the job ended with, when it ended failed; null otherwise.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// When the cancellation of the job was requested — the instant its <c>cancel-job</c> request
    /// was recorded — or <see langword="null"/> for a job that was not being cancelled. It is the
    /// measurement's start, so the cancellation latency is reported from the consumer's request.
    /// </summary>
    public DateTimeOffset? CancellationRequestedAt { get; init; }
}
