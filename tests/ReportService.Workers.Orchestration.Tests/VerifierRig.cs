using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// Everything one verification needs: a storage root unique to the test with the
/// real Filesystem provider in it, the in-memory upload records, the verifier
/// under test, a clock the test advances, and the log the verifier wrote. Uploads
/// can be handed over already verified as the API would leave them, so a case
/// says only what it changes.
/// </summary>
internal sealed class VerifierRig : IDisposable
{
    /// <summary>The consumer every record of this rig belongs to.</summary>
    public const string ConsumerId = "dev-consumer";

    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero);

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private readonly LoggerFactory _loggerFactory;
    private readonly OrchestrationMetrics _metrics;
    private bool _disposed;

    /// <summary>Creates the rig with the limits a case allows.</summary>
    /// <param name="maxStoredBytes">The most stored bytes an upload may hold.</param>
    /// <param name="maxUncompressedBytes">The most content bytes a body may decompress to.</param>
    /// <param name="ticking">Whether the clock advances on every read, so progress is reportable.</param>
    public VerifierRig(long maxStoredBytes = 8 << 20, long maxUncompressedBytes = 8 << 20, bool ticking = false)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Root);

        Logs = new LogCapture();
        _loggerFactory = new LoggerFactory([Logs]);
        var filesystem = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Root },
            Root,
            TimeProvider.System,
            _loggerFactory.CreateLogger<FilesystemObjectStore>());
        Objects = new RecordingObjectStore(filesystem);
        Options = new VerificationOptions
        {
            MaxStoredBytes = maxStoredBytes,
            MaxUncompressedBytes = maxUncompressedBytes,
        };
        Uploads = new InMemoryUploadStore();
        Clock = new SteppingTimeProvider(ticking);
        _metrics = new OrchestrationMetrics();
        Verifier = new UploadVerifier(
            Uploads,
            Objects,
            Microsoft.Extensions.Options.Options.Create(Options),
            Clock,
            _loggerFactory.CreateLogger<UploadVerifier>(),
            _metrics);
    }

    /// <summary>The storage root of this test.</summary>
    public string Root { get; }

    /// <summary>The storage port the verifier uses, counting what it was asked to do.</summary>
    public RecordingObjectStore Objects { get; }

    /// <summary>The upload records.</summary>
    public InMemoryUploadStore Uploads { get; }

    /// <summary>The limits the verifier enforces.</summary>
    public VerificationOptions Options { get; }

    /// <summary>The clock the verifier reads.</summary>
    public SteppingTimeProvider Clock { get; }

    /// <summary>The verifier under test.</summary>
    public UploadVerifier Verifier { get; }

    /// <summary>Every line logged while the test ran.</summary>
    public LogCapture Logs { get; }

    /// <summary>The key an attempt of <paramref name="uploadId"/> writes under.</summary>
    /// <param name="uploadId">The upload the attempt belongs to.</param>
    /// <param name="attempt">A distinct attempt label.</param>
    public static ObjectKey AttemptKey(string uploadId, string attempt = "attempt") =>
        ObjectKey.FromSegments("snapshots", uploadId, attempt);

    /// <summary>
    /// Records an upload as the API leaves it after a confirmation: created,
    /// receiving, what arrived, and verifying.
    /// </summary>
    /// <param name="uploadId">Its identifier.</param>
    /// <param name="stored">The bytes stored and recorded.</param>
    /// <param name="encoding">How the body was declared encoded.</param>
    /// <param name="declaredSha256">What the consumer declared, when it declared something.</param>
    /// <param name="outcome">How the transfer ended.</param>
    /// <param name="writeObject">Whether the bytes are written to the storage root.</param>
    /// <param name="stagingRecorded">Whether the transfer recorded the object's size and hash.</param>
    /// <param name="confirm">Whether the upload is confirmed, which is what makes it verifying.</param>
    public async Task<UploadRecord> GivenVerifyingAsync(
        string uploadId,
        byte[] stored,
        UploadContentEncoding encoding,
        string? declaredSha256 = null,
        UploadContentOutcome outcome = UploadContentOutcome.Complete,
        bool writeObject = true,
        bool stagingRecorded = true,
        bool confirm = true)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = new UploadRecord(
            uploadId,
            ConsumerId,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            CreatedAt,
            CreatedAt.AddDays(1));

        await Uploads.CreateAsync(record, cancellationToken);
        await Uploads.TryStartTransferAsync(uploadId, ConsumerId, encoding, cancellationToken);

        long? size = stagingRecorded ? stored.Length : null;
        string? sha256 = stagingRecorded ? Convert.ToHexStringLower(SHA256.HashData(stored)) : null;
        if (writeObject)
        {
            using var content = new MemoryStream(stored, writable: false);
            var written = await Objects.WriteAsync(record.StagingKey, content, cancellationToken);
            Assert.True(written.Succeeded, "the staging object of the test had to be written");
            size = written.Value!.Size;
            sha256 = written.Value.Sha256;
        }

        await Uploads.RecordTransferAsync(
            uploadId,
            outcome,
            stored.Length,
            outcome == UploadContentOutcome.Complete ? size : null,
            outcome == UploadContentOutcome.Complete ? sha256 : null,
            cancellationToken);

        if (confirm)
        {
            await Uploads.ConfirmAsync(uploadId, ConsumerId, declaredSha256, ConfirmedAt, cancellationToken);
        }

        return (await Uploads.FindAsync(uploadId, ConsumerId, cancellationToken))!;
    }

    /// <summary>The keys of every object under <paramref name="prefix"/>.</summary>    /// <param name="prefix">The key prefix to list.</param>
    public async Task<IReadOnlyList<string>> ListAsync(string prefix)
    {
        var keys = new List<string>();
        await foreach (var info in Objects.ListAsync(prefix, TestContext.Current.CancellationToken))
        {
            keys.Add(info.Key.ToString());
        }

        return keys;
    }

    /// <summary>A verifier over another upload records, keeping this rig's storage, limits and clock.</summary>
    /// <param name="uploads">The records the verifier reads and commits into.</param>
    public UploadVerifier VerifierOver(IUploadStore uploads) => new(
        uploads,
        Objects,
        Microsoft.Extensions.Options.Options.Create(Options),
        Clock,
        _loggerFactory.CreateLogger<UploadVerifier>(),
        _metrics);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _metrics.Dispose();
        _loggerFactory.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }
}

/// <summary>
/// The storage port, delegating to the real Filesystem provider and recording
/// what it was asked to do — reads opened, writes, deletions — so a case can
/// assert that nothing was read, nothing was overwritten and what was removed.
/// It can also be told to answer with a chosen failure, or to report a write as
/// successful although its source failed, which is the case the caller has to
/// survive.
/// </summary>
internal sealed class RecordingObjectStore : IObjectStore
{
    private readonly IObjectStore _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The store that does the work.</param>
    public RecordingObjectStore(IObjectStore inner) => _inner = inner;

    /// <summary>How many reads were opened.</summary>
    public int Opens { get; private set; }

    /// <summary>How many writes were asked for.</summary>
    public int Writes { get; private set; }

    /// <summary>The keys a deletion was asked for, in order.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>When set, a read answers this outcome instead of reading.</summary>
    public StorageOutcome? ReadFailure { get; set; }

    /// <summary>When set, a write answers this outcome instead of writing.</summary>
    public StorageOutcome? WriteFailure { get; set; }

    /// <summary>When set, a write whose source failed reports success anyway.</summary>
    public bool ReportWriteSuccessOnSourceFailure { get; set; }

    /// <inheritdoc />
    public StorageCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public async Task<StorageResult<ObjectWrite>> WriteAsync(
        ObjectKey key,
        Stream content,
        CancellationToken cancellationToken)
    {
        Writes++;
        if (WriteFailure is { } outcome)
        {
            return StorageResult.Failure<ObjectWrite>(outcome, new StorageError("storage.test", "refused by the test"));
        }

        try
        {
            return await _inner.WriteAsync(key, content, cancellationToken);
        }
        catch (Exception) when (ReportWriteSuccessOnSourceFailure)
        {
            return StorageResult.Success(new ObjectWrite(key, 0, new string('0', 64), null));
        }
    }

    /// <inheritdoc />
    public Task<StorageResult<VerifiedRead>> OpenReadAsync(
        ObjectKey key,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        Opens++;
        return ReadFailure is { } outcome
            ? Task.FromResult(StorageResult.Failure<VerifiedRead>(outcome, new StorageError("storage.test", "refused by the test")))
            : _inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
        _inner.GetInfoAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        Deleted.Add(key.ToString());
        return _inner.DeleteAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
        _inner.ListAsync(classPrefix, cancellationToken);
}

/// <summary>
/// A clock a test moves itself: it never follows the wall clock, so a case that
/// turns on "once a second" is decided by the test and not by how fast the
/// machine is. With <c>ticking</c> every read of the clock advances it, which is
/// how a case says "time passed between two blocks".
/// </summary>
internal sealed class SteppingTimeProvider : TimeProvider
{
    private readonly bool _ticking;
    private long _ticks;
    private DateTimeOffset _now = new(2026, 11, 1, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Creates the clock.</summary>
    /// <param name="ticking">Whether each read of the timestamp advances it by two seconds.</param>
    public SteppingTimeProvider(bool ticking = false) => _ticking = ticking;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        if (_ticking)
        {
            Advance(TimeSpan.FromSeconds(2));
        }

        return _ticks;
    }



    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far to move it.</param>
    public void Advance(TimeSpan by)
    {
        _ticks += by.Ticks;
        _now += by;
    }
}
