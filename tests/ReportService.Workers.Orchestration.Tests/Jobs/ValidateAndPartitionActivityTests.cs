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
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The data step of a job, run inside the engine's host against a storage root of the
/// test's own, the in-memory records and a clock the test holds: the whole path from a
/// ready snapshot to a committed partition, the answer a second attempt receives, what
/// it refuses when there is nothing to read, and what the template's own contract
/// refuses — the header, a middle record, the last of two thousand, a format, an
/// undeclared member, a schema that cannot be used and a pin that cannot be sourced.
/// </summary>
public sealed class ValidateAndPartitionActivityTests : IDisposable
{
    private const string ReportJobId = "job-0123456789abcdef0123456789abcdef";

    private const string ConsumerId = "dev-consumer";

    private const string UploadId = UploadIdentifier.Prefix + "0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 12, 30, 400, TimeSpan.Zero);

    /// <summary>The schema the seeded version carries: the header and the records of ordinary cases.</summary>
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
                "amount": { "type": "number" },
                "date": { "type": "string", "format": "date" }
              }
            }
          }
        }
        """;

    private readonly LoggerFactory _loggerFactory;
    private readonly string _root;
    private bool _disposed;

    /// <summary>Creates the rig of one case.</summary>
    public ValidateAndPartitionActivityTests()
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
    public async Task AReadySnapshotBecomesOneBlockAndOneIndexOfTwoRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(2), cancellationToken);
        var writesBefore = Objects.Writes;

        var result = await RunAsync();

        Assert.Equal(1, result.BlockCount);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(1, result.FirstRecordOrdinal);
        Assert.Equal(SingleBlockPartitioner.PartitionerVersion, result.PartitionerVersion);
        Assert.Equal(2, Objects.Writes - writesBefore);
        Assert.Matches("^[0-9a-f]{64}$", result.BlockSha256);
        Assert.Matches("^[0-9a-f]{64}$", result.IndexSha256);

        var partition = await Jobs.FindPartitionAsync(ReportJobId, cancellationToken);
        Assert.NotNull(partition);
        Assert.Equal(2, partition!.RecordCount);
        Assert.Equal(result.IndexKey, partition.IndexObjectKey);
    }

    [Fact]
    public async Task ASecondAttemptReadsWhatTheFirstOneCommitted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(3), cancellationToken);
        var first = await RunAsync();
        var writesAfterFirst = Objects.Writes;

        var second = await RunAsync();

        Assert.Equal(first.BlockKey, second.BlockKey);
        Assert.Equal(first.BlockSha256, second.BlockSha256);
        Assert.Equal(3, second.RecordCount);
        Assert.Equal(writesAfterFirst, Objects.Writes);
    }

    [Fact]
    public async Task ASnapshotWithNoRecordBecomesTheEmptyBlock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(0), cancellationToken);

        var result = await RunAsync();

        Assert.Equal(1, result.BlockCount);
        Assert.Equal(0, result.RecordCount);
        Assert.Equal(1, result.FirstRecordOrdinal);
    }

    [Fact]
    public async Task ASnapshotTheProfileRefusesEndsTheStepWithoutBeingRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Encoding.UTF8.GetBytes("""{"contractVersion":"2.0","header":{},"items":[]}"""), cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.DataInvalid, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("reason=contract-version", failure.Message, StringComparison.Ordinal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ASnapshotLargerThanOneBlockEndsTheStepWithTheCodeOfTheLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(DataContractLimits.BlockRecords + 1), cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.QuotaExceeded, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("limit=block", failure.Message, StringComparison.Ordinal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ASnapshotThatNoLongerMatchesItsRecordEndsTheStepWithoutBeingRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var snapshot = Records(2);
        await SeedAcceptedJobAsync(snapshot, cancellationToken);

        // The object under the recorded key loses its last bytes after it was recorded: the
        // size no longer matches the record, so the pass reaches the end of the stored
        // content and the reader's own verification refuses it.
        var stored = Path.Combine(_root, "snapshots", UploadId, "a");
        await File.WriteAllBytesAsync(stored, snapshot[..^4], cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ASnapshotTheProviderCannotVerifyEndsTheStepWithoutBeingRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(2), cancellationToken);
        Objects.ReadFailure = StorageOutcome.IntegrityFailure;
        Objects.ReadFailureWhen = key => key.ToString().StartsWith("snapshots/", StringComparison.Ordinal);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task ATemporaryStorageFaultKeepsThePortsExceptionSoItIsRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(2), cancellationToken);
        Objects.ReadFailure = StorageOutcome.Transient;
        Objects.ReadFailureWhen = key => key.ToString().StartsWith("snapshots/", StringComparison.Ordinal);

        var failure = await Assert.ThrowsAsync<StorageException>(RunAsync);

        Assert.Equal(StorageOutcome.Transient, failure.Outcome);
    }

    [Fact]
    public async Task AnIndexPageThatNoLongerMatchesItsRecordEndsTheStepWithoutBeingRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var entry = new BlockIndexEntry(
            0,
            $"block/{ReportJobId}/0/attempt",
            new string('a', 64),
            1_024,
            1,
            25,
            512);
        var page = BlockIndexPage.Serialize(new BlockIndexPage(SingleBlockPartitioner.PartitionerVersion, [entry]));
        var indexKey = ObjectKey.FromSegments("index", ReportJobId, "0", "attempt");
        using var content = new MemoryStream(page, writable: false);
        var write = await Objects.WriteAsync(indexKey, content, cancellationToken);
        Assert.True(write.Succeeded);

        // The partition record names the size of the page that exists but not its SHA-256: the
        // page is read to its end and the reader's own verification refuses it.
        await Jobs.CommitPartitionAsync(
            new PartitionRecord(
                ReportJobId,
                SingleBlockPartitioner.PartitionerVersion,
                1,
                25,
                512,
                indexKey.ToString(),
                new string('b', 64),
                write.Value!.Size,
                Now),
            cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("index page", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePartitionThatAlreadyExistsIsAnsweredFromItsIndexPage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var entry = new BlockIndexEntry(
            0,
            $"block/{ReportJobId}/0/attempt",
            new string('a', 64),
            1_024,
            1,
            25,
            512);
        var page = BlockIndexPage.Serialize(new BlockIndexPage(SingleBlockPartitioner.PartitionerVersion, [entry]));
        var indexKey = ObjectKey.FromSegments("index", ReportJobId, "0", "attempt");
        using var content = new MemoryStream(page, writable: false);
        var write = await Objects.WriteAsync(indexKey, content, cancellationToken);
        Assert.True(write.Succeeded);

        await Jobs.CommitPartitionAsync(
            new PartitionRecord(
                ReportJobId,
                SingleBlockPartitioner.PartitionerVersion,
                1,
                25,
                512,
                indexKey.ToString(),
                write.Value!.Sha256,
                write.Value.Size,
                Now),
            cancellationToken);

        var result = await RunAsync();

        Assert.Equal(25, result.RecordCount);
        Assert.Equal(entry.Key, result.BlockKey);
        Assert.Equal(entry.Sha256, result.BlockSha256);
    }

    [Fact]
    public async Task AJobThatIsNotRecordedIsRefusedWithoutBeingRetried()
    {
        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("INPUT_NOT_FOUND", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task AHeaderTheTemplatesSchemaRefusesEndsTheStepWithTheFieldAndTheRule()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var snapshot = Snapshot("""{"title":5}""", """{"sequence":1,"amount":1.5}""");
        await SeedAcceptedJobAsync(snapshot, cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.DataInvalid, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal(
            "the document cannot be used: code=DATA_INVALID reason=schema-violation ordinal=0 offset="
                + OffsetOf(snapshot, "{\"title\":5}").ToString(CultureInfo.InvariantCulture)
                + " path=$.title keyword=type",
            failure.Message);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
        AssertNoMarkerInTheLogs("PLANTED-");

        // The one log line of a refused snapshot names the code, the reason, the ordinal and
        // the field's path and rule — and never a value of the document.
        Assert.Contains(
            Logs.Lines,
            line => line.Contains("reason=schema-violation", StringComparison.Ordinal)
                    && line.Contains("ordinal=0", StringComparison.Ordinal)
                    && line.Contains("path=$.title", StringComparison.Ordinal)
                    && line.Contains("keyword=type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMiddleRecordTheTemplatesSchemaRefusesNamesThatRecordsOrdinal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var snapshot = Snapshot(
            """{"title":"Monthly"}""",
            """{"sequence":1,"amount":1.5}""",
            """{"sequence":2,"amount":"PLANTED-VALUE-middle"}""",
            """{"sequence":3,"amount":3.5}""");
        await SeedAcceptedJobAsync(snapshot, cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.DataInvalid, failure.ErrorType);
        Assert.Contains("reason=schema-violation ordinal=2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("path=$.amount keyword=type", failure.Message, StringComparison.Ordinal);
        Assert.Equal(OffsetOf(snapshot, """{"sequence":2,"amount":"PLANTED-VALUE-middle"}"""), OffsetIn(failure.Message));
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
        AssertNoMarkerInTheLogs("PLANTED-");
    }

    [Fact]
    public async Task TheLastRecordOfTwoThousandIsCheckedAndRefusedByItsOwnOrdinal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = Enumerable.Range(1, DataContractLimits.BlockRecords - 1)
            .Select(ordinal => $$"""{"sequence":{{ordinal}},"amount":{{ordinal}}.5}""")
            .Append("""{"sequence":2000,"amount":"PLANTED-VALUE-last"}""")
            .ToArray();
        await SeedAcceptedJobAsync(Snapshot("""{"title":"Monthly"}""", records), cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Contains("reason=schema-violation ordinal=2000", failure.Message, StringComparison.Ordinal);
        Assert.Contains("path=$.amount keyword=type", failure.Message, StringComparison.Ordinal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
        AssertNoMarkerInTheLogs("PLANTED-VALUE-last");
    }

    [Theory]
    [InlineData("""{"sequence":1,"amount":1.5,"date":"not-a-date"}""", "$.date", "format", "PLANTED-VALUE-format")]
    [InlineData("""{"sequence":1,"amount":1.5,"PLANTED-NAME-member":1}""", "$", "additionalProperties", "PLANTED-NAME-member")]
    public async Task AFormatAndAnUndeclaredMemberAreRefusedAtTheirOwnPaths(
        string offendingRecord,
        string path,
        string keyword,
        string marker)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Snapshot("""{"title":"Monthly"}""", offendingRecord), cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal(DataContractViolation.DataInvalid, failure.ErrorType);
        Assert.Contains("reason=schema-violation ordinal=1", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"path={path} keyword={keyword}", failure.Message, StringComparison.Ordinal);

        // The unexpected member's own name is the producer's text and is never repeated.
        Assert.DoesNotContain(marker, failure.Message, StringComparison.Ordinal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
        AssertNoMarkerInTheLogs("PLANTED-");
    }

    [Fact]
    public async Task AValidSnapshotAndAnEmptyOneAreAdmittedAgainstTheTemplatesSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(2), cancellationToken);

        var valid = await RunAsync();

        Assert.Equal(2, valid.RecordCount);
        Assert.NotNull(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
        AssertNoMarkerInTheLogs("PLANTED-");
    }

    [Fact]
    public async Task AnEmptySnapshotIsAdmittedAndItsHeaderIsStillChecked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Snapshot("""{"title":"Monthly"}"""), cancellationToken);

        var empty = await RunAsync();

        Assert.Equal(0, empty.RecordCount);
        Assert.NotNull(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task AReferenceThatNamesAVersionNoRecordHoldsEndsWithThePinRefusalInOneAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(1), requestedVersion: "9.9.9", cancellationToken);
        var findsBefore = Versions.FindCalls;

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("TEMPLATE_INVALID", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal("TEMPLATE_INVALID rule=pin", failure.Message);
        Assert.Equal(findsBefore + 1, Versions.FindCalls);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task AReferenceByNameNeverReachesTheVersionStoreAndNeverThrows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(1), requestedVersion: null, cancellationToken);

        // The proof is the answer, not a counter: the delivered store counts after its own
        // null guard, so an unchanged count cannot tell a skipped call from one that threw.
        // One attempt, the pin's own message and an application failure and no other.
        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("TEMPLATE_INVALID", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal("TEMPLATE_INVALID rule=pin", failure.Message);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ASchemaThePackageDoesNotCarryEndsWithTheSchemaRefusal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedVersionAsync(TestSchema, cancellationToken, carriesTheSchema: false);
        await SeedAcceptedJobAsync(Records(1), cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("TEMPLATE_INVALID", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal("TEMPLATE_INVALID file=schema.json rule=absent", failure.Message);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ASchemaOutsideTheProfileEndsWithTheRuleThatRefusedIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedVersionAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://templates.invalid/data/planted",
              "properties": { "header": { "type": "object" }, "items": { "items": { "type": "object" } } }
            }
            """,
            cancellationToken);
        await SeedAcceptedJobAsync(Records(1), cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("TEMPLATE_INVALID", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.StartsWith("TEMPLATE_INVALID file=schema.json rule=", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("planted", failure.Message, StringComparison.Ordinal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task APatternTheEvaluatorCannotDecideInTimeEndsWithItsOwnKind()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedVersionAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "properties": {
                "header": { "$ref": "#/$defs/header" },
                "items": { "type": "array", "items": { "$ref": "#/$defs/item" } }
              },
              "$defs": {
                "header": { "type": "object" },
                "item": {
                  "type": "object",
                  "properties": { "description": { "type": "string", "pattern": "^(a+)+$" } }
                }
              }
            }
            """,
            cancellationToken);
        await SeedAcceptedJobAsync(
            Snapshot("""{"title":"Monthly"}""", """{"sequence":1,"amount":1.5,"description":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!"}"""),
            cancellationToken);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(RunAsync);

        Assert.Equal("TEMPLATE_INVALID", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("rule=pattern-time", failure.Message, StringComparison.Ordinal);
        Assert.Null(await Jobs.FindPartitionAsync(ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task TheContractOfOnePackageIsCompiledOnceAcrossTwoJobs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAcceptedJobAsync(Records(2), cancellationToken);
        const string SecondJobId = "job-1123456789abcdef0123456789abcdef";
        var accepted = await Jobs.AcceptAsync(
            new JobAcceptance(
                SecondJobId,
                "output-1123456789abcdef0123456789abcdef",
                ConsumerId,
                UploadId,
                "pilot-simple",
                TemplateReferenceForm.Exact,
                "1.0.0"),
            Now,
            cancellationToken);
        Assert.IsType<JobAcceptanceResult.Accepted>(accepted);

        var first = await RunAsync();
        var second = await RunAsync(SecondJobId);

        Assert.Equal(2, first.RecordCount);
        Assert.Equal(2, second.RecordCount);

        // One compilation for the taken version, however many jobs read it; the version is
        // read once per job, which is what the step's own reference read is.
        Assert.Equal(1, Contracts.CompilationCount);
        Assert.Equal(2, Versions.FindCalls);
    }

    private static long OffsetOf(byte[] document, string text) =>
        document.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text));

    private static long OffsetIn(string message)
    {
        var start = message.IndexOf(" offset=", StringComparison.Ordinal) + " offset=".Length;
        var end = message.IndexOf(' ', start);
        var text = end < 0 ? message[start..] : message[start..end];
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    private void AssertNoMarkerInTheLogs(string marker)
    {
        foreach (var line in Logs.Lines)
        {
            Assert.DoesNotContain(marker, line, StringComparison.Ordinal);
        }
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

    private static byte[] Records(int count)
    {
        var items = string.Join(',', Enumerable.Range(1, count).Select(ordinal => $$"""{"sequence":{{ordinal}},"amount":{{ordinal}}.5}"""));
        return Encoding.UTF8.GetBytes($$"""{"contractVersion":"1.0","header":{"title":"Monthly"},"items":[{{items}}]}""");
    }

    // A snapshot of the profile's shape with a header of the case's own and records of the
    // case's own, so a case says what the schema is asked about.
    private static byte[] Snapshot(string header, params string[] records)
    {
        var items = records.Length == 0 ? "[]" : "[" + string.Join(',', records) + "]";
        return Encoding.UTF8.GetBytes($$"""{"contractVersion":"1.0","header":{{header}},"items":{{items}}}""");
    }

    // The snapshot is written to the storage root and the upload is driven to ready
    // through the upload records' own steps, so the case starts from a state the
    // service can reach.
    private Task SeedAcceptedJobAsync(byte[] snapshot, CancellationToken cancellationToken) =>
        SeedAcceptedJobAsync(snapshot, requestedVersion: "1.0.0", cancellationToken);

    private async Task SeedAcceptedJobAsync(byte[] snapshot, string? requestedVersion, CancellationToken cancellationToken)
    {
        if (Versions.Records.Count == 0)
        {
            await SeedVersionAsync(TestSchema, cancellationToken);
        }

        var uploadId = UploadId;
        var staging = ObjectKey.FromSegments("staging", uploadId, "content");
        var created = Now.AddHours(-1);
        await Uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                ConsumerId,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                staging,
                created,
                created.AddHours(2)),
            cancellationToken);

        var key = ObjectKey.FromSegments("snapshots", uploadId, "a");
        using var content = new MemoryStream(snapshot, writable: false);
        var written = await Objects.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);

        var started = Now.AddMinutes(-20);
        Assert.True(await Uploads.TryStartTransferAsync(uploadId, ConsumerId, UploadContentEncoding.None, started, cancellationToken));
        Assert.True(await Uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, snapshot.Length, written.Value!.Size, written.Value.Sha256, started, cancellationToken));
        Assert.True(await Uploads.ConfirmAsync(
            uploadId, ConsumerId, written.Value.Sha256, started, started, Now.AddHours(5), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await Uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
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
            new JobAcceptance(
                ReportJobId,
                "output-0123456789abcdef0123456789abcdef",
                ConsumerId,
                uploadId,
                "pilot-simple",
                requestedVersion is null ? TemplateReferenceForm.ByName : TemplateReferenceForm.Exact,
                requestedVersion),
            Now,
            cancellationToken);
        Assert.IsType<JobAcceptanceResult.Accepted>(accepted);
    }

    // The taken version the job is pinned to: a package written through the archive the
    // take uses, stored under its own hash, with its schema at the path the record names,
    // and the record of that version in the store the step reads.
    private async Task<TemplateVersionRecord> SeedVersionAsync(
        string schema,
        CancellationToken cancellationToken,
        string schemaRef = "schema.json",
        string version = "1.0.0",
        bool carriesTheSchema = true)
    {
        var files = new List<PackageFileSource>();
        if (carriesTheSchema)
        {
            files.Add(new PackageFileSource(
                schemaRef,
                _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(schema), writable: false))));
        }

        files.Add(new PackageFileSource(
            "template.yaml",
            _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("engine: scriban\n"), writable: false))));

        using var sink = new MemoryStream();
        await TemplatePackageArchive.WriteAsync(files, sink, cancellationToken);
        var bytes = sink.ToArray();
        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = TemplatePackageKeys.ForVersion(ConsumerId, "pilot-simple", version, packageSha256);
        using var content = new MemoryStream(bytes, writable: false);
        var written = await Objects.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);

        var record = TemplateVersionRecords.Sample(
            "pilot-simple",
            version,
            ConsumerId,
            shared: true,
            schemaRef: schemaRef,
            packageSha256: packageSha256) with
        {
            PackageSize = bytes.Length,
            FileCount = files.Count,
            ObjectKey = key,
        };

        Versions.Seed(record);
        return record;
    }

    private Task<PartitionResult> RunAsync() => RunAsync(ReportJobId);

    private Task<PartitionResult> RunAsync(string reportJobId)
    {
        var environment = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with
            {
                WorkflowId = $"report-job/{reportJobId}",
                WorkflowRunId = "run-0123456789abcdef",
                ActivityId = "1",
                ActivityType = ValidateAndPartitionActivity.Name,
                Attempt = 1,
                TaskQueue = PdfOutputActivityOptions.DataQueue,
            },
            Heartbeater = _ => { },
        };
        return environment.RunAsync(() => Activity.RunAsync(new ValidateAndPartitionInput(reportJobId)));
    }
}
