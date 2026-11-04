using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Packages;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The progress the data step writes beside its pass: the count it has read, on its own
/// writer, on the beat's own interval. The interval is a static readonly consumed by a
/// timer no test clock drives, so a case that observes a tick waits the real interval;
/// the pass is held at a record boundary while it does, by a storage read the case
/// releases when it has seen what it waited for. A write that fails is logged and never
/// fails the step, a second attempt cannot lower what the first raised, and a snapshot
/// the reader refuses leaves the count of the records it accepted and no total.
/// </summary>
public sealed class ValidateAndPartitionProgressTests : IDisposable
{
    private const string ReportJobId = "job-0123456789abcdef0123456789abcdef";

    private const string OutputId = "output-0123456789abcdef0123456789abcdef";

    private const string ConsumerId = "dev-consumer";

    private const string UploadId = UploadIdentifier.Prefix + "0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 12, 30, 400, TimeSpan.Zero);

    private const string TestSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "header": { "$ref": "#/$defs/header" },
            "items": { "type": "array", "items": { "$ref": "#/$defs/item" } }
          },
          "$defs": {
            "header": {
              "type": "object",
              "additionalProperties": false,
              "required": ["title"],
              "properties": { "title": { "type": "string" } }
            },
            "item": {
              "type": "object",
              "additionalProperties": false,
              "required": ["sequence", "amount"],
              "properties": {
                "sequence": { "type": "integer", "minimum": 1 },
                "amount": { "type": "number" }
              }
            }
          }
        }
        """;

    private readonly string _root;
    private readonly LogCapture _logs;
    private readonly LoggerFactory _loggerFactory;

    /// <summary>Creates the rig of one case.</summary>
    public ValidateAndPartitionProgressTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_root);
        _logs = new LogCapture();
        _loggerFactory = new LoggerFactory([_logs]);
        Uploads = new InMemoryUploadStore();
        Jobs = new InMemoryJobStore(Uploads);
        Objects = new GatedObjectStore(FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _root },
            _root,
            TimeProvider.System,
            _loggerFactory.CreateLogger<FilesystemObjectStore>()));
    }

    /// <summary>The upload records of this case.</summary>
    public InMemoryUploadStore Uploads { get; }

    /// <summary>The job records of this case.</summary>
    public InMemoryJobStore Jobs { get; }

    /// <summary>The storage port of this case, gated by the case itself.</summary>
    public GatedObjectStore Objects { get; }

    [Fact]
    public async Task TheCountThePassReadIsWrittenOnItsOwnTickAndOnlyWhenItChanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var document = Records(400);
        await SeedAcceptedJobAsync(document, cancellationToken);
        Objects.GateAt(OffsetOf(document, "\"sequence\":301"));

        var run = RunAsync();

        // The pass is held before the 301st record: the tick that follows writes the 300
        // it read. The count is read from the store the consumer would read.
        await Wait.UntilAsync(
            "the progress tick to write the records the pass had read",
            async token => (await Jobs.FindStatusAsync(ReportJobId, ConsumerId, token))?.RecordsRead == 300,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);
        var writes = Jobs.ProgressWriteCalls;
        Assert.True(writes >= 1);

        // While the pass stays held at the same count, the next tick changes nothing and
        // writes again only when the count moves: one more interval is waited in real time.
        await Task.Delay(PdfOutputActivityOptions.HeartbeatEvery + TimeSpan.FromSeconds(1), cancellationToken);
        Assert.Equal(writes, Jobs.ProgressWriteCalls);

        Objects.Release();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(400, result.RecordCount);

        var status = await Jobs.FindStatusAsync(ReportJobId, ConsumerId, cancellationToken);
        Assert.Equal(400, status!.RecordsRead);
        Assert.Equal(400, status.RecordsTotal);
        Assert.Equal(1, status.BlocksTotal);

        // A second attempt cannot lower what the first raised: the store ignores a lower
        // value, and the step that finds the partition reads it instead of writing again.
        Assert.Equal(ProgressWriteOutcome.Unchanged, await Jobs.RecordRecordsReadAsync(ReportJobId, 100, cancellationToken));
        Assert.Equal(400, (await Jobs.FindStatusAsync(ReportJobId, ConsumerId, cancellationToken))!.RecordsRead);
    }

    [Fact]
    public async Task ARefusedSnapshotLeavesTheCountOfTheRecordsItAcceptedAndNoTotal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accepted = Enumerable.Range(1, 41).Select(ordinal => $$"""{"sequence":{{ordinal}},"amount":{{ordinal}}.5}""");
        var refused = Snapshot("""{"title":"Monthly"}""", [.. accepted, """{"sequence":42,"amount":"refused"}"""]);
        await SeedAcceptedJobAsync(refused, cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.DataInvalid, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        var status = await Jobs.FindStatusAsync(ReportJobId, ConsumerId, cancellationToken);
        Assert.Equal(41, status!.RecordsRead);
        Assert.Null(status.RecordsTotal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task AProgressWriteThatFailsIsLoggedAndNeverFailsTheStep()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(400), cancellationToken);
        Objects.GateAt(OffsetOf(Records(400), "\"sequence\":301"));
        Jobs.ProgressWriteFailure = new InvalidOperationException("the records are not reachable");

        var run = RunAsync();
        await Wait.UntilAsync(
            "the progress tick to be refused and logged",
            _ => Task.FromResult(_logs.Lines.Any(line =>
                line.StartsWith("Warning", StringComparison.Ordinal)
                && line.Contains("Progress of job", StringComparison.Ordinal)
                && line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal))),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);

        Objects.Release();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        // The step completed, the partition is durable and the counters were still fixed by
        // the commit: a progress write that failed is not a step that failed.
        Assert.Equal(400, result.RecordCount);
        var status = await Jobs.FindStatusAsync(ReportJobId, ConsumerId, cancellationToken);
        Assert.Equal(400, status!.RecordsTotal);
    }

    public void Dispose()
    {
        Objects.Dispose();
        _loggerFactory.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    private Task<PartitionResult> RunAsync()
    {
        var activity = new ValidateAndPartitionActivity(
            Jobs,
            Uploads,
            Objects,
            Versions,
            Contracts,
            new FixedTimeProvider(Now),
            _loggerFactory.CreateLogger<ValidateAndPartitionActivity>(),
            Path.Combine(_root, "scratch"));
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                WorkflowId = $"report-job/{ReportJobId}",
                WorkflowRunId = "run-0123456789abcdef",
                ActivityId = "1",
                ActivityType = ValidateAndPartitionActivity.Name,
                Attempt = 1,
                TaskQueue = PdfOutputActivityOptions.DataQueue,
            },
            Heartbeater = _ => { },
        };
        return environment.RunAsync(() => activity.RunAsync(new ValidateAndPartitionInput(ReportJobId)));
    }

    private static byte[] Records(int count)
    {
        var items = string.Join(',', Enumerable.Range(1, count).Select(ordinal => $$"""{"sequence":{{ordinal}},"amount":{{ordinal}}.5}"""));
        return Encoding.UTF8.GetBytes($$"""{"contractVersion":"1.0","header":{"title":"Monthly"},"items":[{{items}}]}""");
    }

    private static byte[] Snapshot(string header, params string[] records)
    {
        var items = records.Length == 0 ? "[]" : "[" + string.Join(',', records) + "]";
        return Encoding.UTF8.GetBytes($$"""{"contractVersion":"1.0","header":{{header}},"items":{{items}}}""");
    }

    private static long OffsetOf(byte[] document, string text) => document.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text));

    private async Task SeedAcceptedJobAsync(byte[] snapshot, CancellationToken cancellationToken)
    {
        await SeedVersionAsync(cancellationToken);

        var staging = ObjectKey.FromSegments("staging", UploadId, "content");
        var created = Now.AddHours(-1);
        await Uploads.CreateAsync(
            new UploadRecord(
                UploadId,
                ConsumerId,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                staging,
                created,
                created.AddHours(2)),
            cancellationToken);

        var key = ObjectKey.FromSegments("snapshots", UploadId, "a");
        using var content = new MemoryStream(snapshot, writable: false);
        var written = await Objects.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);

        var started = Now.AddMinutes(-20);
        Assert.True(await Uploads.TryStartTransferAsync(UploadId, ConsumerId, UploadContentEncoding.None, started, cancellationToken));
        Assert.True(await Uploads.RecordTransferAsync(
            UploadId, UploadContentOutcome.Complete, snapshot.Length, written.Value!.Size, written.Value.Sha256, started, cancellationToken));
        Assert.True(await Uploads.ConfirmAsync(
            UploadId, ConsumerId, written.Value.Sha256, started, started, Now.AddHours(5), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await Uploads.CommitReadyAsync(
                new SnapshotRecord(
                    UploadId,
                    ConsumerId,
                    key,
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    written.Value.Sha256,
                    snapshot.Length,
                    written.Value.Sha256,
                    written.Value.Size,
                    started),
                started,
                cancellationToken));

        Assert.IsType<JobAcceptanceResult.Accepted>(await Jobs.AcceptAsync(
            new JobAcceptance(ReportJobId, OutputId, ConsumerId, UploadId, "pilot-simple", TemplateReferenceForm.Exact, "1.0.0"),
            Now,
            cancellationToken));
    }

    private async Task SeedVersionAsync(CancellationToken cancellationToken)
    {
        var files = new List<PackageFileSource>
        {
            new("schema.json", _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(TestSchema), writable: false))),
            new("template.yaml", _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("engine: scriban\n"), writable: false))),
        };

        using var sink = new MemoryStream();
        await TemplatePackageArchive.WriteAsync(files, sink, cancellationToken);
        var bytes = sink.ToArray();
        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = TemplatePackageKeys.ForVersion(ConsumerId, "pilot-simple", "1.0.0", packageSha256);
        using var content = new MemoryStream(bytes, writable: false);
        var written = await Objects.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);

        Versions.Seed(TemplateVersionRecords.Sample(
            "pilot-simple",
            "1.0.0",
            ConsumerId,
            shared: true,
            schemaRef: "schema.json",
            packageSha256: packageSha256) with
        {
            PackageSize = bytes.Length,
            FileCount = files.Count,
            ObjectKey = key,
        });
    }

    private InMemoryTemplateVersionStore Versions { get; } = new();

    private PreparedDataContractCache Contracts { get; } = new();

    /// <summary>
    /// The storage port of this case: every read and write is the filesystem's, except the
    /// content of the snapshot the case gates, which stops at a byte offset until the case
    /// releases it.
    /// </summary>
    public sealed class GatedObjectStore : IObjectStore
    {
        private readonly IObjectStore _inner;
        private readonly Lock _gate = new();
        private GatedReadStream? _gated;

        /// <summary>Wraps <paramref name="inner"/>, which does all the work.</summary>
        /// <param name="inner">The store under this gate.</param>
        public GatedObjectStore(IObjectStore inner) => _inner = inner;

        /// <inheritdoc />
        public StorageCapabilities Capabilities => _inner.Capabilities;

        /// <summary>Holds every snapshot read at <paramref name="offset"/> until <see cref="Release"/>.</summary>
        /// <param name="offset">The byte offset the read stops before.</param>
        public void GateAt(long offset)
        {
            lock (_gate)
            {
                _gated = new GatedReadStream(offset);
            }
        }

        /// <summary>Lets the gated read continue to its end.</summary>
        public void Release()
        {
            lock (_gate)
            {
                _gated?.Release();
            }
        }

        /// <inheritdoc />
        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            _inner.WriteAsync(key, content, cancellationToken);

        /// <inheritdoc />
        public async Task<StorageResult<VerifiedRead>> OpenReadAsync(
            ObjectKey key,
            long expectedSize,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            var read = await _inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded || !key.ToString().StartsWith("snapshots/", StringComparison.Ordinal))
            {
                return read;
            }

            GatedReadStream? gate;
            lock (_gate)
            {
                gate = _gated;
            }

            return gate is null
                ? read
                : StorageResult.Success(new VerifiedRead(
                    new GatedStream(gate, read.Value!.Content),
                    read.Value.Size,
                    read.Value.Sha256));
        }

        /// <inheritdoc />
        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            _inner.GetInfoAsync(key, cancellationToken);

        /// <inheritdoc />
        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(key, cancellationToken);

        /// <inheritdoc />
        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            _inner.ListAsync(classPrefix, cancellationToken);

        /// <inheritdoc />
        public void Dispose() => (_inner as IDisposable)?.Dispose();

        /// <summary>The gate itself: an offset and the task that opens it once.</summary>
        private sealed class GatedReadStream(long offset)
        {
            private readonly TaskCompletionSource _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public long Offset { get; } = offset;

            public Task Released => _release.Task;

            public void Release() => _release.TrySetResult();
        }

        // A forward-only read that stops before the gate's offset and then waits for the
        // release. The offset falls inside the record the pass has not accepted yet, so the
        // count stays where the case can observe it.
        private sealed class GatedStream : Stream
        {
            private readonly GatedReadStream _gate;
            private readonly Stream _inner;
            private long _position;

            public GatedStream(GatedReadStream gate, Stream inner)
            {
                _gate = gate;
                _inner = inner;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position < _gate.Offset)
                {
                    var allowed = (int)Math.Min(buffer.Length, _gate.Offset - _position);
                    var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
                    _position += read;
                    return read;
                }

                await _gate.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
                var rest = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                _position += rest;
                return rest;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
