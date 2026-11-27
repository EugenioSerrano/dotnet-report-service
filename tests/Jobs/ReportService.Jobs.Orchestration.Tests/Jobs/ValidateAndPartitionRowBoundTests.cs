using System.Diagnostics;
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
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The row bound inside the data step: a PDF job whose detail data carries a string value above
/// the bound ends the pass with the refusal kept on its partition record and returned with the
/// result - the first offending record in snapshot order, the field's path, its decoded size and
/// the bound, never a value. The pass itself succeeds and goes on to the end of the snapshot, so
/// a contract error anywhere, the record's own size limit and the single block's limit still win
/// as job-level failures; a second attempt answers the committed refusal without reading the
/// snapshot again; a header is never a row, and a job that asked for no PDF is not scanned.
/// </summary>
public sealed class ValidateAndPartitionRowBoundTests : IDisposable
{
    private const string ReportJobId = "job-0123456789abcdef0123456789abcdef";

    private const string ConsumerId = "dev-consumer";

    private const string UploadId = UploadIdentifier.Prefix + "0123456789abcdef0123456789abcdef";

    private const string OutputId = "output-0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 20, 40, 0, TimeSpan.Zero);

    private readonly LoggerFactory _loggerFactory;
    private readonly string _root;
    private bool _disposed;

    /// <summary>Creates the rig of one case.</summary>
    public ValidateAndPartitionRowBoundTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_root);

        Logs = new LogCapture();
        _loggerFactory = new LoggerFactory([Logs]);
        Objects = new RecordingObjectStore(FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _root },
            _root,
            TimeProvider.System,
            _loggerFactory.CreateLogger<FilesystemObjectStore>()));
        Uploads = new InMemoryUploadStore();
        Jobs = new InMemoryJobStore(Uploads);
        Versions = new InMemoryTemplateVersionStore();
        Contracts = new PreparedDataContractCache();
        Activity = new ValidateAndPartitionActivity(
            Jobs,
            Uploads,
            Objects,
            Versions,
            Contracts,
            new FixedTimeProvider(Now),
            _loggerFactory.CreateLogger<ValidateAndPartitionActivity>(),
            Path.Combine(_root, "scratch"));
    }

    /// <summary>Every line logged while the case ran.</summary>
    public LogCapture Logs { get; }

    /// <summary>The storage port of this case, counting what it was asked to do.</summary>
    internal RecordingObjectStore Objects { get; }

    /// <summary>The upload records of this case.</summary>
    public InMemoryUploadStore Uploads { get; }

    /// <summary>The job records of this case.</summary>
    public InMemoryJobStore Jobs { get; }

    /// <summary>The taken versions of this case.</summary>
    public InMemoryTemplateVersionStore Versions { get; }

    /// <summary>The contracts this case's step prepared.</summary>
    public PreparedDataContractCache Contracts { get; }

    /// <summary>The step under test.</summary>
    public ValidateAndPartitionActivity Activity { get; }

    [Fact]
    public async Task TheLastRecordOfFifteenHundredWithAFieldAboveTheBoundRefusesThePdfOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = Enumerable.Range(1, 1_500)
            .Select(ordinal => ordinal == 1_500 ? Item(ordinal, UpperCase(262_145)) : Item(ordinal, "A short description"))
            .ToArray();

        // The block's own byte maximum is not crossed before the offending record.
        AssertBlockBytesStayUnderTheirMaximum(records);

        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        var result = await RunAsync();

        var refusal = Assert.IsType<PdfOutputRefusal>(result.PdfRefusal);
        Assert.Equal(DataContractViolation.RowBoundExceeded, refusal.Code);
        Assert.Equal(
            "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset="
                + OffsetOf(await ReadSnapshotAsync(), records[1_499]).ToString(CultureInfo.InvariantCulture)
                + " path=$.description",
            refusal.Reason);

        var partition = await Jobs.FindPartitionAsync(ReportJobId, cancellationToken);
        Assert.NotNull(partition);
        Assert.Equal(DataContractViolation.RowBoundExceeded, partition!.PdfRefusalCode);
        Assert.Equal(refusal.Reason, partition.PdfRefusalReason);
        Assert.Equal(1_500L, partition.RecordCount);
    }

    [Fact]
    public async Task AFieldOfOneHundredThousandThreeByteCharactersIsRefusedWithItsSizeInBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = Enumerable.Range(1, 3)
            .Select(ordinal => ordinal == 3 ? Item(ordinal, new string('\u20AC', 100_000)) : Item(ordinal, "Short"))
            .ToArray();
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        var result = await RunAsync();

        Assert.Equal("ROW_BOUND_EXCEEDED", result.PdfRefusal!.Code);
        Assert.Contains("reason=field-too-large size=300000 bound=262144", result.PdfRefusal.Reason, StringComparison.Ordinal);
        Assert.Contains("ordinal=3", result.PdfRefusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFirstOfSeveralOversizedRecordsInSnapshotOrderIsTheOneReported()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = Enumerable.Range(1, 9)
            .Select(ordinal => ordinal is 2 or 5 or 9 ? Item(ordinal, UpperCase(262_145)) : Item(ordinal, "Short"))
            .ToArray();
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        var result = await RunAsync();

        Assert.Contains("ordinal=2", result.PdfRefusal!.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinal=5", result.PdfRefusal.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinal=9", result.PdfRefusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordOfExactlyTheBoundInMultiByteCharactersIsNotRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = new[] { Item(1, new string('\u00E9', 131_072)), Item(2, "Short"), Item(3, "Short") };
        AssertBlockBytesStayUnderTheirMaximum(records);
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        var result = await RunAsync();

        Assert.Null(result.PdfRefusal);
        Assert.NotNull(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task AHeaderFieldAboveTheBoundIsNotARowAndIsNotRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = Enumerable.Range(1, 3).Select(ordinal => Item(ordinal, "Short")).ToArray();
        await SeedAcceptedJobAsync(Snapshot(Header(UpperCase(262_145)), records), cancellationToken);

        var result = await RunAsync();

        Assert.Null(result.PdfRefusal);
    }

    [Fact]
    public async Task AnEmptySnapshotIsNotRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly")), cancellationToken);

        var result = await RunAsync();

        Assert.Null(result.PdfRefusal);
        Assert.Equal(0, result.RecordCount);
    }

    [Fact]
    public async Task AValueUnderAMemberTheSchemaDoesNotDeclareIsNamedByTheStarPath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedVersionAsync(OpenItemSchema, cancellationToken);
        var record = """{"sequence":1,"notes":""" + Quoted(UpperCase(262_145)) + "}";
        await SeedAcceptedJobAsync(Snapshot("""{"title":"Monthly"}""", record), cancellationToken);

        var result = await RunAsync();

        Assert.NotNull(result.PdfRefusal);
        Assert.EndsWith(" path=$.*", result.PdfRefusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContractErrorAfterAnOversizedRecordFailsTheJobAtTheContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = Enumerable.Range(1, 5)
            .Select(ordinal => ordinal switch
            {
                2 => Item(ordinal, UpperCase(262_145)),
                5 => """{"sequence":5,"description":5,"quantity":1,"unitPrice":1.0,"amount":1.0}""",
                _ => Item(ordinal, "Short"),
            })
            .ToArray();
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.DataInvalid, failure.ErrorType);
        Assert.Contains("reason=schema-violation ordinal=5", failure.Message, StringComparison.Ordinal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ASnapshotTruncatedAfterAnOversizedRecordFailsTheJobAtTheReader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var document = Snapshot(Header("Monthly"), Item(1, UpperCase(262_145)), Item(2, "Short"));
        await SeedAcceptedJobAsync(document[..^8], cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.DataInvalid, failure.ErrorType);
        Assert.Contains("reason=truncated", failure.Message, StringComparison.Ordinal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ARecordOverTheRecordLimitFailsTheJobAtItsOwnLimitBeforeTheBound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // One record of more than a mebibyte made of fields no larger than the token limit:
        // the record's own limit refuses it, and the over-bound field inside never reaches the
        // row bound.
        var fields = new StringBuilder("{\"sequence\":1");
        fields.Append(",\"w\":").Append(Quoted(UpperCase(300_000)));
        for (var index = 0; index < 8; index++)
        {
            fields.Append(",\"f").Append(index).Append("\":").Append(Quoted(UpperCase(100_000)));
        }

        fields.Append('}');
        Assert.True(Encoding.UTF8.GetByteCount(fields.ToString()) > DataContractLimits.RecordBytes);
        await SeedVersionAsync(OpenItemSchema, cancellationToken);
        await SeedAcceptedJobAsync(Snapshot("""{"title":"Monthly"}""", fields.ToString()), cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.RecordTooLarge, failure.ErrorType);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ASnapshotBeyondTheBlockMaximumKeepsTheRowBoundRefusalAndCommitsThePartition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = Enumerable.Range(1, DataContractLimits.BlockRecords + 1)
            .Select(ordinal => Item(ordinal, "Short"))
            .ToArray();
        records[0] = Item(1, UpperCase(262_145));
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        // The block limits split the snapshot instead of refusing it; the row bound is the
        // refusal the output keeps, and the partition is committed for it to travel with.
        var result = await RunAsync();

        Assert.Equal(DataContractViolation.RowBoundExceeded, result.PdfRefusal!.Code);
        Assert.Contains("ordinal=1", result.PdfRefusal.Reason, StringComparison.Ordinal);
        var partition = await Jobs.FindPartitionAsync(ReportJobId, cancellationToken);
        Assert.NotNull(partition);
        Assert.True(partition!.BlockCount >= 2, "the snapshot is admitted as more than one block");
    }

    [Fact]
    public async Task TheBlockByteMaximumIsSplitAndTheLaterOversizedRecordIsTheRowBoundRefusal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = Enumerable.Range(1, 6)
            .Select(ordinal => ordinal switch
            {
                6 => Item(ordinal, UpperCase(262_145)),
                <= 4 => Item(ordinal, UpperCase(200_000)),
                _ => Item(ordinal, UpperCase(250_000)),
            })
            .ToArray();

        // The first five records are within the row bound, and together they cross the block's
        // byte maximum: the blocks are cut rather than refused, and the sixth record's bound
        // is the refusal the output keeps.
        var admitted = records[..5].Sum(record => (long)Encoding.UTF8.GetByteCount(record));
        Assert.True(admitted > DataContractLimits.BlockRecordBytes, $"the first five records hold {admitted} bytes");
        Assert.All(records[..5], record => Assert.True(Encoding.UTF8.GetByteCount(record) < PdfRowBound.Bytes));
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        var result = await RunAsync();

        Assert.Equal(DataContractViolation.RowBoundExceeded, result.PdfRefusal!.Code);
        Assert.Contains("ordinal=6", result.PdfRefusal.Reason, StringComparison.Ordinal);
        var partition = await Jobs.FindPartitionAsync(ReportJobId, cancellationToken);
        Assert.NotNull(partition);
        Assert.True(partition!.BlockCount >= 2, "the byte maximum is split, never refused");
    }

    [Fact]
    public async Task AThreeHundredKilobyteFieldInARecordUnderOneMebibyteIsTheRowBoundAndNotTheRecordLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = new[] { Item(1, UpperCase(300_000)) };
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        var result = await RunAsync();

        Assert.Equal(DataContractViolation.RowBoundExceeded, result.PdfRefusal!.Code);
        Assert.Contains("size=300000", result.PdfRefusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondAttemptAnswersTheCommittedRefusalWithoutReadingTheSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = new[] { Item(1, UpperCase(262_145)), Item(2, "Short") };
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);
        var first = await RunAsync();
        var opensAfterFirst = Objects.Opens;

        // A snapshot read this attempt reached would fail and end the step: only the partition
        // it already committed is read.
        Objects.ReadFailure = StorageOutcome.Permanent;
        Objects.ReadFailureWhen = key => key.ToString().StartsWith("snapshots/", StringComparison.Ordinal);

        var second = await RunAsync();

        Assert.Equal(first.PdfRefusal, second.PdfRefusal);
        Assert.Equal(opensAfterFirst + 1, Objects.Opens);
    }

    [Fact]
    public async Task AJobThatAskedForNoPdfIsNotScanned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = new[] { Item(1, UpperCase(262_145)), Item(2, "Short") };
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);
        var activity = new ValidateAndPartitionActivity(
            new NoPdfOutputJobStore(Jobs),
            Uploads,
            Objects,
            Versions,
            Contracts,
            new FixedTimeProvider(Now),
            _loggerFactory.CreateLogger<ValidateAndPartitionActivity>(),
            Path.Combine(_root, "scratch"));

        var result = await RunInAsync(activity);

        Assert.Null(result.PdfRefusal);
        Assert.NotNull(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task TheRefusalThePassKeptNamesNoPartOfTheValueInItsReasonItsLogOrItsSpan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var value = UpperCase(262_145);
        var records = new[] { Item(1, "Short"), Item(2, value) };
        await SeedAcceptedJobAsync(Snapshot(Header("Monthly"), records), cancellationToken);

        using var span = new Activity("row-bound-case").Start();
        var result = await RunAsync();
        span.Stop();

        var reason = result.PdfRefusal!.Reason;
        AssertNoWindowOf(value, reason);
        foreach (var line in Logs.Lines)
        {
            AssertNoWindowOf(value, line);
        }

        var attributes = span.Tags
            .Select(tag => tag.Value)
            .Concat(span.TagObjects.Select(tag => tag.Value?.ToString()))
            .Where(value => !string.IsNullOrEmpty(value))
            .ToList();
        Assert.Contains("report.pdf.refusal_code", span.Tags.Select(tag => tag.Key).Concat(span.TagObjects.Select(tag => tag.Key)));
        Assert.Contains("report.pdf.refusal_ordinal", span.Tags.Select(tag => tag.Key).Concat(span.TagObjects.Select(tag => tag.Key)));
        foreach (var attribute in attributes)
        {
            AssertNoWindowOf(value, attribute!);
        }
    }

    [Fact]
    public async Task TheSerialisedResultOfTheWorstCasePathStaysUnderFourKibibytes()
    {
        var path = "$." + new string('a', 1_022);
        var reason = RowBoundDiagnostic.For(1_500, 48_213, 262_145, PdfRowBound.Bytes, path);
        var result = new PartitionResult(1, 1, 2_000, 1_048_576, 1, "block/job/0/attempt", 1_048_576, new string('a', 64), "index/job/0/attempt", 4_096, new string('b', 64))
        {
            PdfRefusal = new PdfOutputRefusal(DataContractViolation.RowBoundExceeded, reason),
        };

        var payload = new DefaultPayloadConverter().ToPayload(result);

        Assert.True(payload.Data.Length < 4 * 1_024, $"the serialised result is {payload.Data.Length} bytes");
        Assert.True(Encoding.UTF8.GetByteCount(reason) <= 1_172, $"the reason is {Encoding.UTF8.GetByteCount(reason)} bytes");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Contracts.Dispose();
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

    // The pilot's own data contract, read from the repository: its item declares the
    // description with no length limit and admits no member beside the declared ones.
    private static string PilotSchema => File.ReadAllText(
        Path.Combine(RepositoryPaths.Root, "samples", "templates", "pilot-simple", "schema.json"));

    // A contract whose item declares nothing beside its sequence and admits what it does not
    // declare, so a value under an undeclared member is valid data and over the bound.
    private const string OpenItemSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "header": { "type": "object" },
            "items": { "type": "array", "items": { "$ref": "#/$defs/item" } }
          },
          "$defs": {
            "item": {
              "type": "object",
              "required": ["sequence"],
              "properties": { "sequence": { "type": "integer", "minimum": 1 } }
            }
          }
        }
        """;

    private static void AssertBlockBytesStayUnderTheirMaximum(IEnumerable<string> records)
    {
        var total = records.Sum(record => (long)Encoding.UTF8.GetByteCount(record));
        Assert.True(total < DataContractLimits.BlockRecordBytes, $"the block would hold {total} record bytes");
    }

    private static void AssertNoWindowOf(string value, string text)
    {
        for (var start = 0; start + 16 <= text.Length; start++)
        {
            var window = text.AsSpan(start, 16);
            Assert.True(
                value.AsSpan().IndexOf(window) < 0,
                $"a window of the diagnostic occurs in the value: {window.ToString()}");
        }
    }

    // A value of upper-case Latin letters from a fixed seed: no window of a diagnostic can
    // occur in it by chance, and no character of it is a digit or a punctuation mark.
    private static string UpperCase(int length)
    {
        var random = new Random(20261119);
        var value = new char[length];
        for (var index = 0; index < length; index++)
        {
            value[index] = (char)('A' + random.Next(26));
        }

        return new string(value);
    }

    private static string Header(string title) =>
        "{\"datasetId\":\"pilot-simple\",\"reportTitle\":" + Quoted(title)
        + ",\"generatedAt\":\"2026-11-19T20:00:00Z\",\"seed\":0,\"currency\":\"EUR\","
        + "\"customer\":{\"id\":\"c-1\",\"name\":\"Pilot\"},\"totals\":{\"recordCount\":0,\"amount\":0.0}}";

    private static string Item(int sequence, string description) =>
        $$"""{"sequence":{{sequence}},"description":{{Quoted(description)}},"quantity":1,"unitPrice":1.0,"amount":1.0}""";

    private static string Quoted(string value) => "\"" + value + "\"";

    private static byte[] Snapshot(string header, params string[] records) =>
        Encoding.UTF8.GetBytes($$"""{"contractVersion":"1.0","header":{{header}},"items":[{{string.Join(',', records)}}]}""");

    private static long OffsetOf(byte[] document, string text) =>
        document.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text));

    private Task<byte[]> ReadSnapshotAsync() =>
        File.ReadAllBytesAsync(Path.Combine(_root, "snapshots", UploadId, "a"));

    // The snapshot is written to the storage root and the upload is driven to ready through the
    // upload records' own steps, so the case starts from a state the service can reach.
    private async Task SeedAcceptedJobAsync(byte[] snapshot, CancellationToken cancellationToken)
    {
        if (Versions.Records.Count == 0)
        {
            await SeedVersionAsync(PilotSchema, cancellationToken);
        }

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

        var accepted = await Jobs.AcceptAsync(
            new JobAcceptance(ReportJobId, [new RequestedOutput(OutputId, OutputFormat.Pdf)], ConsumerId, UploadId, "pilot-simple", TemplateReferenceForm.Exact, "1.0.0",
            ReportService.Testing.AcceptanceIdentity.For(UploadId)),
            Now,
            cancellationToken);
        Assert.IsType<JobAcceptanceResult.Accepted>(accepted);
    }

    // The taken version the job is pinned to: a package written through the archive the take
    // uses, stored under its own hash, with its schema at the path the record names.
    private async Task SeedVersionAsync(string schema, CancellationToken cancellationToken)
    {
        var files = new List<PackageFileSource>
        {
            new("schema.json", _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(schema), writable: false))),
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

    private Task<PartitionResult> RunAsync() => RunInAsync(Activity);

    private Task<PartitionResult> RunInAsync(ValidateAndPartitionActivity activity)
    {
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

    /// <summary>
    /// The delivered job store, with the one read a job's steps take answering the same job
    /// whose only output is of another form: the case that proves a job which asked for no PDF
    /// is not scanned.
    /// </summary>
    private sealed class NoPdfOutputJobStore(IJobStore inner) : IJobStore
    {
        public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.AcceptAsync(acceptance, now, cancellationToken);

        public Task<IdempotencyLookup> FindByIdempotencyKeyAsync(
        string consumerId,
        string idempotencyKey,
        RequestFingerprint fingerprint,
        CancellationToken cancellationToken) =>
        inner.FindByIdempotencyKeyAsync(consumerId, idempotencyKey, fingerprint, cancellationToken);
    public async Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
            await inner.FindForWorkflowAsync(reportJobId, cancellationToken) is { } state
                ? state with { Outputs = [.. state.Outputs.Select(output => output with { Format = OutputFormat.Html })] }
                : null;

        public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindTemplateReferenceAsync(reportJobId, cancellationToken);

        public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, string? message, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailJobAsync(reportJobId, code, reason, message, now, cancellationToken);

        public Task<TemplatePinState> PinTemplateAsync(string reportJobId, TemplatePin pin, DateTimeOffset now, CancellationToken cancellationToken, TemplateLocaleDefaults? presentation) =>
            inner.PinTemplateAsync(reportJobId, pin, now, cancellationToken, presentation);

        public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.BeginInputWaitAsync(reportJobId, cancellationToken);

        public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.EndInputWaitAsync(reportJobId, cancellationToken);

        public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) =>
            inner.FindInputWaitingJobIdsAsync(uploadId, cancellationToken);

        public Task<TransitionJobResult> TransitionJobAsync(string reportJobId, IReadOnlyList<ReportJobStatus> from, ReportJobStatus to, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TransitionJobAsync(reportJobId, from, to, now, cancellationToken);

        public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TransitionOutputAsync(outputId, now, cancellationToken);

        public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) =>
            inner.CommitPartitionAsync(partition, cancellationToken);

        public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) =>
            inner.FindPartitionAsync(reportJobId, cancellationToken);

        public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken) =>
            inner.CommitPartAsync(commit, cancellationToken);

        public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) =>
            inner.FindPartAsync(outputId, blockIndex, cancellationToken);

        public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) =>
            inner.PublishAsync(artifact, cancellationToken);

        public Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            inner.ListArtifactsAsync(reportJobId, consumerId, cancellationToken);

        public Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindArtifactAsync(reportJobId, artifactId, consumerId, cancellationToken);

        public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, string? reason, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailOutputAsync(outputId, code, reason, now, cancellationToken);

        public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FinalizeJobAsync(reportJobId, now, cancellationToken);

        public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindStatusAsync(reportJobId, consumerId, cancellationToken);

        public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) =>
            inner.RecordRecordsReadAsync(reportJobId, recordsRead, cancellationToken);

        public Task<AttemptCauseOutcome> RecordAttemptCauseAsync(AttemptCause cause, CancellationToken cancellationToken) =>
            inner.RecordAttemptCauseAsync(cause, cancellationToken);

        public Task<AttemptCause?> FindLastAttemptCauseAsync(string outputId, int blockIndex, CancellationToken cancellationToken) =>
            inner.FindLastAttemptCauseAsync(outputId, blockIndex, cancellationToken);
    }
}
