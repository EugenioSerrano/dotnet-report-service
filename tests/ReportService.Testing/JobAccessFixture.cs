using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.Testing;

/// <summary>
/// The records a consumer may read, seeded once for the cases of the job reads: three
/// registered consumers, one of them inactive, a job of consumer A in every state the
/// service reaches, an artifact that is live, one whose expiry has passed and one whose
/// stored object is gone, a job of each of the other two consumers, and the well-formed
/// identifiers that name nothing. Every job is seeded through the stores' own writers —
/// an upload through the upload store's steps, an acceptance, the state transitions, a
/// publication — never by writing a record directly, so each state is one the service
/// can actually reach. The artifact's bytes are written through the storage port, so the
/// size and the hash the row carries are the store's own figures for those bytes.
/// </summary>
public sealed class JobAccessFixture
{
    /// <summary>The consumer whose jobs most cases read.</summary>
    public const string ConsumerA = "consumer-a";

    /// <summary>The other active consumer of the registry.</summary>
    public const string ConsumerB = "consumer-b";

    /// <summary>The consumer the registry carries as inactive.</summary>
    public const string ConsumerC = "consumer-c";

    /// <summary>A well-formed job identifier no job was accepted with.</summary>
    public const string AbsentJobId = "job-ffffffffffffffffffffffffffffffff";

    /// <summary>A well-formed artifact identifier no output was published under.</summary>
    public const string AbsentArtifactId = "artifact-ffffffffffffffffffffffffffffffff";

    /// <summary>The values a request may send in the identity header that cannot be one.</summary>
    public static readonly IReadOnlyList<string> MalformedJobIds =
    [
        "job-XYZ",
        "job-0123456789abcdef0123456789abcdef0",
        "job-0123456789abcdef0123456789abcdeF",
    ];

    /// <summary>The artifact values of the same shapes.</summary>
    public static readonly IReadOnlyList<string> MalformedArtifactIds =
    [
        "artifact-XYZ",
        "artifact-0123456789abcdef0123456789abcdef0",
        "artifact-0123456789abcdef0123456789abcdeF",
    ];

    private static readonly byte[] ReportContent =
    [
        .. "the verified bytes of a published report,"u8,
        .. new byte[512],
    ];

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly InMemoryJobStore _jobs;
    private readonly InMemoryUploadStore _uploads;
    private readonly IObjectStore _objects;
    private readonly DateTimeOffset _now;
    private int _sequence;

    /// <summary>Prepares the fixture over the stores and the storage the host serves.</summary>
    /// <param name="jobs">The job records, as the host's own in-memory store.</param>
    /// <param name="uploads">The upload records the acceptances are judged against.</param>
    /// <param name="objects">The storage port the artifact bytes are written through.</param>
    /// <param name="now">The instant the host's clock reads, at the precision a stored row carries.</param>
    public JobAccessFixture(InMemoryJobStore jobs, InMemoryUploadStore uploads, IObjectStore objects, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(objects);
        _jobs = jobs;
        _uploads = uploads;
        _objects = objects;
        _now = now;
    }

    /// <summary>Consumer A's job that has not started.</summary>
    public SeededJob Queued { get; private set; } = null!;

    /// <summary>Consumer A's job that is being rendered.</summary>
    public SeededJob Running { get; private set; } = null!;

    /// <summary>Consumer A's job that ended failed with the quota code.</summary>
    public SeededJob Failed { get; private set; } = null!;

    /// <summary>Consumer A's succeeded job with one live artifact.</summary>
    public SeededJob Succeeded { get; private set; } = null!;

    /// <summary>Consumer A's succeeded job whose artifact's expiry has passed.</summary>
    public SeededJob Expired { get; private set; } = null!;

    /// <summary>Consumer A's succeeded job whose live artifact's stored object is gone.</summary>
    public SeededJob Gone { get; private set; } = null!;

    /// <summary>Consumer B's succeeded job with one live artifact.</summary>
    public SeededJob SucceededOfB { get; private set; } = null!;

    /// <summary>Consumer C's succeeded job: published while it was active.</summary>
    public SeededJob SucceededOfC { get; private set; } = null!;

    /// <summary>Seeds every record the cases read, in the order they depend on.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        Queued = await AcceptedAsync(ConsumerA, cancellationToken);
        Running = await AcceptedAsync(ConsumerA, cancellationToken);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await _jobs.TransitionJobAsync(Running.JobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, _now, cancellationToken)).Outcome);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await _jobs.TransitionJobAsync(Running.JobId, [ReportJobStatus.Validating], ReportJobStatus.Running, _now, cancellationToken)).Outcome);

        Failed = await AcceptedAsync(ConsumerA, cancellationToken);
        Assert.Equal(
            JobFailureOutcome.Applied,
            await _jobs.FailJobAsync(Failed.JobId, "QUOTA_EXCEEDED", "block", _now, cancellationToken));

        Succeeded = await AcceptedAsync(ConsumerA, cancellationToken);
        Succeeded = await PublishedAsync(Succeeded, ReportContent, _now, _now.Add(Retention), cancellationToken);

        Expired = await AcceptedAsync(ConsumerA, cancellationToken);
        Expired = await PublishedAsync(
            Expired,
            ReportContent,
            _now.AddDays(-8),
            _now.AddDays(-1),
            cancellationToken);

        Gone = await AcceptedAsync(ConsumerA, cancellationToken);
        Gone = await PublishedAsync(Gone, ReportContent, _now, _now.Add(Retention), cancellationToken);

        SucceededOfB = await PublishedAsync(
            await AcceptedAsync(ConsumerB, cancellationToken),
            ReportContent,
            _now,
            _now.Add(Retention),
            cancellationToken);

        SucceededOfC = await PublishedAsync(
            await AcceptedAsync(ConsumerC, cancellationToken),
            ReportContent,
            _now,
            _now.Add(Retention),
            cancellationToken);
    }

    /// <summary>Accepts and publishes one more succeeded job for a consumer of its own.</summary>
    /// <param name="consumerId">The consumer the job belongs to.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>The seeded job with its live artifact.</returns>
    public Task<SeededJob> AddSucceededAsync(string consumerId, CancellationToken cancellationToken) =>
        PublishedAsync(
            null,
            consumerId,
            ReportContent,
            _now,
            _now.Add(Retention),
            cancellationToken);

    private async Task<SeededJob> AcceptedAsync(string consumerId, CancellationToken cancellationToken)
    {
        var uploadId = await ReadyUploadAsync(consumerId, cancellationToken);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            consumerId,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0");
        var accepted = await _jobs.AcceptAsync(acceptance, _now, cancellationToken);
        Assert.IsType<JobAcceptanceResult.Accepted>(accepted);
        return new SeededJob(acceptance.ReportJobId, acceptance.OutputId, null, null);
    }

    private async Task<SeededJob> PublishedAsync(
        SeededJob? job,
        string consumerId,
        byte[] content,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken) =>
        await PublishedAsync(job ?? await AcceptedAsync(consumerId, cancellationToken), content, createdAt, expiresAt, cancellationToken);

    private async Task<SeededJob> PublishedAsync(
        SeededJob job,
        byte[] content,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var objectKey = $"outputs/{job.JobId}/{job.OutputId}/{Guid.NewGuid().ToString("N")}";
        using var source = new MemoryStream(content, writable: false);
        var written = await _objects.WriteAsync(ObjectKey.Create(objectKey), source, cancellationToken);
        Assert.True(written.Succeeded, $"the case's bytes had to be stored: {written.Outcome} {written.Error?.Message}");

        Assert.Equal(TransitionOutcome.Applied, await _jobs.TransitionOutputAsync(job.OutputId, _now, cancellationToken));
        var artifact = new ArtifactRecord(
            ArtifactIdentifier.Create(),
            job.JobId,
            job.OutputId,
            OutputFormat.Pdf,
            "application/pdf",
            objectKey,
            written.Value!.Sha256,
            written.Value.Size,
            3,
            createdAt,
            expiresAt);
        Assert.Equal(PublishOutcome.Published, (await _jobs.PublishAsync(artifact, cancellationToken)).Outcome);
        Assert.Equal(JobFinalizationOutcome.Applied, (await _jobs.FinalizeJobAsync(job.JobId, _now, cancellationToken)).Outcome);
        return job with { Artifact = artifact, Content = content };
    }

    private async Task<string> ReadyUploadAsync(string consumerId, CancellationToken cancellationToken)
    {
        var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
        var record = new UploadRecord(
            uploadId,
            consumerId,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            _now.AddHours(-1),
            _now.AddHours(1));
        await _uploads.CreateAsync(record, cancellationToken);
        Assert.True(await _uploads.TryStartTransferAsync(
            uploadId, consumerId, UploadContentEncoding.None, _now.AddMinutes(-30), cancellationToken));
        Assert.True(await _uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 800, Hash, _now.AddMinutes(-20), cancellationToken));
        Assert.True(await _uploads.ConfirmAsync(
            uploadId, consumerId, Hash, _now.AddMinutes(-21), _now.AddMinutes(-20), _now.AddHours(5), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await _uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    consumerId,
                    ObjectKey.FromSegments("snapshots", uploadId, "a"),
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    Hash,
                    900,
                    Hash,
                    900,
                    _now.AddMinutes(-10)),
                _now.AddMinutes(-10),
                cancellationToken));
        return uploadId;
    }

    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";
}

/// <summary>One job the fixture seeded, with the artifact published for it when there is one.</summary>
/// <param name="JobId">The job's identifier.</param>
/// <param name="OutputId">The identifier of the one output the acceptance created.</param>
/// <param name="Artifact">The artifact published from that output, or <see langword="null"/> for a job that published none.</param>
/// <param name="Content">The bytes written for the artifact, or <see langword="null"/> for none.</param>
public sealed record SeededJob(string JobId, string OutputId, ArtifactRecord? Artifact, byte[]? Content);
