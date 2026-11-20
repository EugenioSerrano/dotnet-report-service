extern alias orchestration;

using System.Globalization;
using System.Text.Json.Nodes;
using ReportService.Jobs;
using ReportService.Testing;
using ReportService.Testing.Limits;
using Temporalio.Api.History.V1;
using Temporalio.Common;
using PdfOutputActivities = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivities;
using PdfOutputStepContracts = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputStepContracts;
using ValidateAndPartitionActivity = orchestration::ReportService.Workers.Orchestration.Jobs.ValidateAndPartitionActivity;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// A report wider than one block, driven as a real job through the product's own API and its
/// own processes: one PostgreSQL 18.6 database with the product's migrations, one Temporal
/// 1.9.1 engine, one storage root, the product's API, orchestration worker and render worker on
/// their real registrations, and the sample template published into that root and read through
/// the source class. Each case starts the orchestration worker with the record count it asserts,
/// because the setting is read when the worker starts and cannot vary inside one process.
/// <para>
/// A refusal is asserted as one state: the job and its output failed with the same code and the
/// same stored reason, no page was rendered and nothing was published, no partition record and
/// no part commit exist, the history holds one attempt of the data step and none of the render or
/// publish steps, and no value of the snapshot appears in the reason or in the lines the run
/// logged. The document that fits is asserted as the positive control: it prints, publishes once
/// and verifies by size and SHA-256.
/// </para>
/// </summary>
[Trait("Requires", "printing")]
public sealed class SingleBlockLimitTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    /// <summary>The limit every case of the record family configures.</summary>
    private const int ConfiguredBound = 1_000;

    /// <summary>The limit the byte cases configure, above the block's own maximum.</summary>
    private const int ConfiguredAboveTheMaximum = 5_000;

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public SingleBlockLimitTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AThousandRecordsUnderAConfiguredThousandPrintAndPublishOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: ConfiguredBound,
            cancellationToken: cancellationToken);

        var (reportJobId, job) = await PrintAsync(rig, LimitSnapshots.RecordsAtBound(), cancellationToken);

        // The refusal refuses nothing the profile admits: one block, one artifact, one PDF, and
        // every one of the thousand records in it. The positive control of this level -
        // PrintSmallReportTests' own thousand-record case - runs beside this class unchanged.
        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal("SUCCEEDED", job.OutputStatus);
        Assert.Null(job.ErrorCode);
        Assert.Null(job.ErrorReason);
        Assert.Null(job.OutputErrorReason);
        Assert.Equal(1, job.BlockCount);
        Assert.Equal(1_000, job.RecordCount);
        Assert.NotNull(job.ArtifactId);

        var bytes = await rig.ReadArtifactAsync(job, cancellationToken);
        Assert.Equal(job.SizeBytes, bytes.LongLength);
        Assert.Equal(job.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        Assert.Equal(1, (await rig.Jobs.FindPartitionAsync(reportJobId, cancellationToken))!.BlockCount);
    }

    [Fact]
    public async Task OneRecordOverTheConfiguredLimitIsRefusedNamingTheConfiguredValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: ConfiguredBound,
            cancellationToken: cancellationToken);

        var (reportJobId, job) = await PrintAsync(rig, LimitSnapshots.RecordsOverTheBound(), cancellationToken);

        await AssertRefusedAsync(
            rig,
            reportJobId,
            job,
            "dimension=records applied=1000",
            ordinal: 1_001,
            cancellationToken);
    }

    [Fact]
    public async Task AFiftyThousandRecordSnapshotStopsAtTheFirstRecordOverTheLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: ConfiguredBound,
            cancellationToken: cancellationToken);

        var (reportJobId, job) = await PrintAsync(rig, LimitSnapshots.RecordsBeyondBoth(), cancellationToken);

        // The pass stops at the first record over the limit: the refusal names record 1 001 and
        // never a total it did not read.
        await AssertRefusedAsync(
            rig,
            reportJobId,
            job,
            "dimension=records applied=1000",
            ordinal: 1_001,
            cancellationToken);
    }

    [Fact]
    public async Task AConfiguredLimitAboveTheBlockMaximumIsRefusedAtTheMaximumItApplied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: ConfiguredAboveTheMaximum,
            cancellationToken: cancellationToken);

        var (reportJobId, job) = await PrintAsync(rig, LimitSnapshots.RecordsOverTheMaximum(), cancellationToken);

        await AssertRefusedAsync(
            rig,
            reportJobId,
            job,
            "dimension=records applied=2000",
            ordinal: 2_001,
            cancellationToken);

        // The operator's configuration is not what a consumer reads: neither the stored reason
        // nor the output's names it.
        Assert.DoesNotContain("5000", job.ErrorReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("5000", job.OutputErrorReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SixHundredRecordsCrossingTheByteBoundAreRefusedNamingTheBytesApplied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: ConfiguredAboveTheMaximum,
            cancellationToken: cancellationToken);

        var (reportJobId, job) = await PrintAsync(rig, LimitSnapshots.BytesCrossingAtRecord573(), cancellationToken);

        await AssertRefusedAsync(
            rig,
            reportJobId,
            job,
            "dimension=bytes applied=1048576",
            ordinal: 573,
            cancellationToken);
    }

    [Fact]
    public async Task TheByteBoundaryPairIsAdmittedAtTheBoundAndRefusedOneByteOver()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: ConfiguredAboveTheMaximum,
            cancellationToken: cancellationToken);

        var (_, admitted) = await PrintAsync(rig, LimitSnapshots.BytesAtTheBound(), cancellationToken);
        Assert.Equal("SUCCEEDED", admitted.Status);
        Assert.Equal("SUCCEEDED", admitted.OutputStatus);
        Assert.NotNull(admitted.ArtifactId);
        Assert.Equal(64, admitted.RecordCount);

        var (reportJobId, refused) = await PrintAsync(rig, LimitSnapshots.BytesOneOverTheBound(), cancellationToken);
        await AssertRefusedAsync(
            rig,
            reportJobId,
            refused,
            "dimension=bytes applied=1048576",
            ordinal: 64,
            cancellationToken);
    }

    [Fact]
    public async Task ARecordOverOneMebibyteIsRefusedByItsOwnCodeBeforeTheSingleBlockLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: ConfiguredBound,
            cancellationToken: cancellationToken);

        var (reportJobId, job) = await PrintAsync(rig, LimitSnapshots.RecordsWithOneOverTheRecordLimit(), cancellationToken);

        // The per-record bound runs before this one and wins: the job ends with the one-record
        // code, on the job and on its output, and still nothing is printed or published.
        Assert.Equal("FAILED", job.Status);
        Assert.Equal("RECORD_TOO_LARGE", job.ErrorCode);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal("RECORD_TOO_LARGE", job.OutputErrorCode);
        Assert.Equal(job.ErrorReason, job.OutputErrorReason);
        await AssertNothingWasPublishedAsync(rig, reportJobId, job, cancellationToken);
    }

    // The five facts of a refusal, asserted together: both rows carried the same code and the
    // same stored reason, the pass ran once and no page was rendered, and nothing a consumer
    // could reach was written.
    private static async Task AssertRefusedAsync(
        PrintingRig rig,
        string reportJobId,
        JobStateRow job,
        string expectedReason,
        int ordinal,
        CancellationToken cancellationToken)
    {
        Assert.Equal("FAILED", job.Status);
        Assert.Equal("QUOTA_EXCEEDED", job.ErrorCode);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal("QUOTA_EXCEEDED", job.OutputErrorCode);
        Assert.Equal(job.ErrorReason, job.OutputErrorReason);

        // The stored reason is the data step's whole sentence, with the dimension and the value
        // it applied before the limit's own token, and the position the pass stopped at.
        var reason = job.ErrorReason;
        Assert.NotNull(reason);
        var prefix = "the document cannot be used: code=QUOTA_EXCEEDED reason=" + expectedReason
            + " limit=block ordinal=" + ordinal.ToString(CultureInfo.InvariantCulture) + " offset=";
        Assert.StartsWith(prefix, reason, StringComparison.Ordinal);
        var offset = reason![prefix.Length..];
        Assert.True(offset.Length > 0 && offset.All(char.IsAsciiDigit), $"the reason closes with an offset, and '{offset}' is not one");

        await AssertNothingWasPublishedAsync(rig, reportJobId, job, cancellationToken);
        await AssertNoPageWasRenderedAsync(rig, reportJobId, cancellationToken);
    }

    private static async Task AssertNothingWasPublishedAsync(
        PrintingRig rig,
        string reportJobId,
        JobStateRow job,
        CancellationToken cancellationToken)
    {
        Assert.Null(await rig.Jobs.FindPartitionAsync(reportJobId, cancellationToken));
        Assert.Null(await rig.Jobs.FindPartAsync(job.OutputId!, 0, cancellationToken));

        var artifacts = await rig.Jobs.ListArtifactsAsync(reportJobId, PrintingRig.Consumer, cancellationToken);
        Assert.True(artifacts is null || artifacts.Artifacts.Count == 0, "a refused job publishes no artifact");

        Assert.Null(job.ArtifactId);
        // The refusals share the rig's storage root with an admitted job of the same class, so
        // the namespace checked is the refused job's own output and never the root: a file under
        // another job's output is that job's publication, not this one's.
        var written = rig.OutputObjectKeys(reportJobId, job.OutputId!);
        Assert.True(
            written.Count == 0,
            $"a refused job wrote an object under its own output namespace: {string.Join(", ", written)}");
    }

    private static async Task AssertNoPageWasRenderedAsync(
        PrintingRig rig,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        var history = await rig.HistoryAsync(reportJobId, cancellationToken);

        // One attempt of the data step: a retry would appear as a second scheduled activity.
        Assert.Single(Activities(history, ValidateAndPartitionActivity.Name));
        Assert.Empty(Activities(history, PdfOutputStepContracts.RenderPdfBlock));
        Assert.Empty(Activities(history, PdfOutputActivities.PublishOutputName));
    }

    private static IReadOnlyList<HistoryEvent> Activities(WorkflowHistory history, string name) =>
        [.. history.Events
            .Where(entry => entry.EventType == Temporalio.Api.Enums.V1.EventType.ActivityTaskScheduled)
            .Where(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                name,
                StringComparison.Ordinal))];

    private static async Task<(string ReportJobId, JobStateRow Job)> PrintAsync(
        PrintingRig rig,
        byte[] snapshot,
        CancellationToken cancellationToken)
    {
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(snapshot, cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        // The refused state is written once and read back whole; the same document refused
        // twice would be asserted by a second submission, and the pass is deterministic.
        AssertNoValueOf(snapshot, job, rig, reportJobId);
        return (reportJobId, job);
    }

    // No value of the report reaches the job's own record or the lines the run logged for it:
    // every window of sixteen characters of the snapshot's text is looked for, and none may
    // occur.
    private static void AssertNoValueOf(byte[] snapshot, JobStateRow job, PrintingRig rig, string reportJobId)
    {
        var values = StringsOf(snapshot).Where(value => value.Length >= 16).ToList();
        if (values.Count == 0)
        {
            return;
        }

        AssertNoWindow(job.ErrorReason, values, "the job's stored reason");
        AssertNoWindow(job.OutputErrorReason, values, "the output's stored reason");
        foreach (var line in rig.Logs.Lines.Where(line => line.Contains(reportJobId, StringComparison.Ordinal)))
        {
            AssertNoWindow(line, values, "a log line of the job");
        }
    }

    private static void AssertNoWindow(string? text, IReadOnlyList<string> values, string where)
    {
        if (text is null)
        {
            return;
        }

        foreach (var value in values)
        {
            for (var start = 0; start + 16 <= value.Length; start += 8)
            {
                var window = value.Substring(start, 16);
                Assert.DoesNotContain(window, text, StringComparison.Ordinal);
            }
        }
    }

    private static IEnumerable<string> StringsOf(byte[] document)
    {
        var root = JsonNode.Parse(document)!.AsObject();
        return Walk(root);

        static IEnumerable<string> Walk(JsonNode? node) => node switch
        {
            JsonObject value => value.SelectMany(member => Walk(member.Value)),
            JsonArray value => value.SelectMany(Walk),
            JsonValue value when value.TryGetValue<string>(out var text) => [text],
            _ => [],
        };
    }
}
