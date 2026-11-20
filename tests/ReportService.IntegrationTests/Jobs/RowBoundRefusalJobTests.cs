extern alias orchestration;

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ReportService.Uploads;
using ReportService.Testing;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Common;
using PdfOutputActivities = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivities;
using PdfOutputStepContracts = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputStepContracts;
using JobTransitionActivities = orchestration::ReportService.Workers.Orchestration.Jobs.JobTransitionActivities;
using ValidateAndPartitionActivity = orchestration::ReportService.Workers.Orchestration.Jobs.ValidateAndPartitionActivity;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The PDF row bound, driven as a real job through the product's own API, orchestration worker
/// and render worker on their real registrations: one PostgreSQL 18.6 database with the
/// product's migrations, one Temporal 1.9.1 engine, one storage root, and the sample template
/// taken into that root. Each refused snapshot is asserted as one state: the job and its PDF
/// output failed with the same code and the same stored reason naming the record, the field, its
/// size and the bound, the partition row holds the refusal while its block and index are
/// intermediates nobody published, no part commit, artifact or output object exists, the job's
/// history holds no output child and no render or publish step, and no part of the field's value
/// occurs in either reason or in any line the run logged. The two documents the bound does not
/// refuse are the controls: their output child starts and their block is rendered and committed.
/// </summary>
[Trait("Requires", "printing")]
public sealed class RowBoundRefusalJobTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    /// <summary>The record count the worker applies, above every snapshot this class submits.</summary>
    private const int SingleBlockRecordLimit = 2_000;

    /// <summary>The bound every refusal names.</summary>
    private const int Bound = 262_144;

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public RowBoundRefusalJobTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AFieldOfTwoHundredFiftySixKibibytesAndOneByteInTheLastRecordIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: SingleBlockRecordLimit,
            cancellationToken: cancellationToken);

        var value = UpperCase(262_145);
        var document = Snapshot(1_500, ordinal => ordinal == 1_500 ? value : "A short description");
        var (reportJobId, job) = await SubmitAsync(rig, document, cancellationToken);

        await AssertRefusedAsync(rig, reportJobId, job, ordinal: 1_500, size: 262_145, value, document, cancellationToken);
    }

    [Fact]
    public async Task TheSameRefusalIsReachedWhenTheSnapshotIsStoredCompressed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: SingleBlockRecordLimit,
            cancellationToken: cancellationToken);

        var value = UpperCase(262_145);
        var document = Snapshot(1_500, ordinal => ordinal == 1_500 ? value : "A short description");
        var uploadId = await rig.ReadyUploadAsync(document, UploadContentEncoding.Gzip, cancellationToken);
        var reportJobId = await rig.SubmitAsync(uploadId, PrintingRig.First, cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        await AssertRefusedAsync(rig, reportJobId, job, ordinal: 1_500, size: 262_145, value, document, cancellationToken);
    }

    [Fact]
    public async Task AFieldOfOneHundredThousandThreeByteCharactersIsRefusedByItsSizeInBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: SingleBlockRecordLimit,
            cancellationToken: cancellationToken);

        var value = new string('\u20AC', 100_000);
        var document = Snapshot(3, ordinal => ordinal == 3 ? value : "A short description");
        var (reportJobId, job) = await SubmitAsync(rig, document, cancellationToken);

        await AssertRefusedAsync(rig, reportJobId, job, ordinal: 3, size: 300_000, value, document, cancellationToken);
    }

    [Fact]
    public async Task TheFirstOfThreeOversizedRecordsInSnapshotOrderIsTheOneReported()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: SingleBlockRecordLimit,
            cancellationToken: cancellationToken);

        var value = UpperCase(262_145);
        var document = Snapshot(9, ordinal => ordinal is 2 or 5 or 9 ? value : "A short description");
        var (reportJobId, job) = await SubmitAsync(rig, document, cancellationToken);

        await AssertRefusedAsync(rig, reportJobId, job, ordinal: 2, size: 262_145, value, document, cancellationToken);
    }

    [Fact]
    public async Task ARecordOfExactlyTheBoundInTwoByteCharactersStartsItsOutputChild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: SingleBlockRecordLimit,
            cancellationToken: cancellationToken);

        var value = new string('\u00E9', 131_072);
        var document = Snapshot(3, ordinal => ordinal == 3 ? value : "A short description");
        var (reportJobId, job) = await SubmitAsync(rig, document, cancellationToken);

        await AssertNotRefusedByTheBoundAsync(rig, reportJobId, job, cancellationToken);
    }

    [Fact]
    public async Task AHeaderFieldAboveTheBoundWithRecordsWithinItStartsItsOutputChild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            singleBlockRecordLimit: SingleBlockRecordLimit,
            cancellationToken: cancellationToken);

        var document = Snapshot(3, ordinal => "A short description", reportTitle: UpperCase(262_145));
        var (reportJobId, job) = await SubmitAsync(rig, document, cancellationToken);

        await AssertNotRefusedByTheBoundAsync(rig, reportJobId, job, cancellationToken);
    }

    // The refusal's whole state, asserted once: both rows carry the same code and the same
    // stored reason, the partition holds the refusal, no page was rendered and nothing a
    // consumer could reach was written, and no part of the value was stored or logged.
    private static async Task AssertRefusedAsync(
        PrintingRig rig,
        string reportJobId,
        JobStateRow job,
        int ordinal,
        long size,
        string value,
        byte[] document,
        CancellationToken cancellationToken)
    {
        Assert.Equal("FAILED", job.Status);
        Assert.Equal("ROW_BOUND_EXCEEDED", job.ErrorCode);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal("ROW_BOUND_EXCEEDED", job.OutputErrorCode);
        Assert.Equal(job.ErrorReason, job.OutputErrorReason);

        var reason = job.ErrorReason;
        Assert.NotNull(reason);
        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size={size} bound={Bound} limit=row-bound ordinal={ordinal} offset=");
        Assert.StartsWith(prefix, reason, StringComparison.Ordinal);
        var closing = reason![prefix.Length..];
        Assert.EndsWith(" path=$.description", closing, StringComparison.Ordinal);
        var offset = closing[..^" path=$.description".Length];
        Assert.True(offset.Length > 0 && offset.All(char.IsAsciiDigit), $"the reason closes with an offset, and '{offset}' is not one");

        // The verdict is durable on the partition, and the block and index it describes are
        // intermediates: they exist, and nobody was given them.
        var partition = await rig.Jobs.FindPartitionAsync(reportJobId, cancellationToken);
        Assert.NotNull(partition);
        Assert.Equal("ROW_BOUND_EXCEEDED", partition!.PdfRefusalCode);
        Assert.Equal(reason, partition.PdfRefusalReason);
        Assert.NotEmpty(rig.ReadStoredObject(partition.IndexObjectKey));
        Assert.True(
            Directory.Exists(Path.Combine(rig.StorageRoot, "block", reportJobId, "0")),
            "the block of the refused partition is an intermediate object and it exists");

        Assert.Equal(0L, await rig.CountPartCommitsAsync(job.OutputId!, cancellationToken));
        Assert.Null(await rig.Jobs.FindPartAsync(job.OutputId!, 0, cancellationToken));
        var artifacts = await rig.Jobs.ListArtifactsAsync(reportJobId, PrintingRig.Consumer, cancellationToken);
        Assert.True(artifacts is null || artifacts.Artifacts.Count == 0, "a refused job publishes no artifact");
        Assert.Null(job.ArtifactId);
        Assert.False(
            Directory.Exists(Path.Combine(rig.StorageRoot, "outputs", reportJobId)),
            "a refused job wrote an object under the output namespace");

        // No output child was started, so no render and no publication can appear: the history
        // is the one job's, and it shows the refusal it was ended with.
        var history = await rig.HistoryAsync(reportJobId, cancellationToken);
        Assert.Single(Activities(history, ValidateAndPartitionActivity.Name));
        Assert.Single(Activities(history, PdfOutputActivities.FindPdfOutputName));
        var failure = Assert.Single(Activities(history, JobTransitionActivities.FailOutputName));
        var input = failure.ActivityTaskScheduledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8();
        Assert.Contains("ROW_BOUND_EXCEEDED", input, StringComparison.Ordinal);
        Assert.Contains("path=$.description", input, StringComparison.Ordinal);
        Assert.DoesNotContain(history.Events, entry => entry.EventType == EventType.StartChildWorkflowExecutionInitiated);
        Assert.Empty(Activities(history, PdfOutputStepContracts.RenderPdfBlock));
        Assert.Empty(Activities(history, PdfOutputActivities.PublishOutputName));

        // No value of the demand reached a record or a line of the run: the field's own text is
        // looked for, sixteen characters at a time, in both stored reasons and in every line.
        AssertNoWindow(window => Assert.DoesNotContain(window, reason, StringComparison.Ordinal), value, "the job's stored reason");
        AssertNoWindow(window => Assert.DoesNotContain(window, job.OutputErrorReason!, StringComparison.Ordinal), value, "the output's stored reason");
        foreach (var line in rig.Logs.Lines.Where(line => line.Contains(reportJobId, StringComparison.Ordinal)))
        {
            AssertNoWindow(window => Assert.DoesNotContain(window, line, StringComparison.Ordinal), value, "a log line of the job");
        }

        // The PDF worker printed nothing for this output, and the child that never existed is
        // the reason: no line of the run names a print of it.
        Assert.DoesNotContain(
            rig.Logs.Lines,
            line => line.Contains(job.OutputId!, StringComparison.Ordinal) && line.Contains("printed", StringComparison.Ordinal));

        AssertNoPartOfTheDocumentWasLogged(rig, reportJobId, document);
    }

    // The document the bound does not refuse: its child starts, its block is rendered and
    // committed, and the terminal outcome is recorded by the case's assertion of the code.
    private static async Task AssertNotRefusedByTheBoundAsync(
        PrintingRig rig,
        string reportJobId,
        JobStateRow job,
        CancellationToken cancellationToken)
    {
        Assert.NotEqual("ROW_BOUND_EXCEEDED", job.OutputErrorCode);
        var history = await rig.HistoryAsync(reportJobId, cancellationToken);

        // The child is started for this output: the render the bound would have prevented runs.
        Assert.Contains(history.Events, entry => entry.EventType == EventType.StartChildWorkflowExecutionInitiated);
        Assert.Empty(Activities(history, JobTransitionActivities.FailOutputName));
        Assert.True(
            await rig.CountPartCommitsAsync(job.OutputId!, cancellationToken) >= 1,
            "the output of a document within the bound commits its printed block");
    }

    // Every sixteen characters of a string the document carries, looked for in the places a
    // refusal may write to; the field's value is the largest of them and the report's header and
    // its other fields are small.
    private static void AssertNoPartOfTheDocumentWasLogged(PrintingRig rig, string reportJobId, byte[] document)
    {
        var values = StringsOf(document).Where(text => text.Length >= 16).ToList();
        foreach (var line in rig.Logs.Lines.Where(line => line.Contains(reportJobId, StringComparison.Ordinal)))
        {
            foreach (var text in values)
            {
                AssertNoWindow(window => Assert.DoesNotContain(window, line, StringComparison.Ordinal), text, "a log line of the job");
            }
        }
    }

    private static void AssertNoWindow(Action<string> assert, string text, string where)
    {
        for (var start = 0; start + 16 <= text.Length; start += 8)
        {
            assert(text.Substring(start, 16));
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

    private static IReadOnlyList<HistoryEvent> Activities(WorkflowHistory history, string name) =>
        [.. history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Where(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                name,
                StringComparison.Ordinal))];

    private static async Task<(string ReportJobId, JobStateRow Job)> SubmitAsync(
        PrintingRig rig,
        byte[] document,
        CancellationToken cancellationToken)
    {
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(document, cancellationToken),
            PrintingRig.First,
            cancellationToken);
        return (reportJobId, await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken));
    }

    // The pilot's own document, with one record's description written by the case: every other
    // field comes from the template's sample, so the schema admits the record and the bound is
    // the only rule that can refuse it.
    private static byte[] Snapshot(
        int records,
        Func<int, string> description,
        string? reportTitle = null)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", PrintingRig.Template, "sample.json")))!;
        var prototype = sample["items"]!.AsArray();
        var items = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(prototype[index % prototype.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;
            item["description"] = description(index + 1);
            items.Add(item);
        }

        sample["items"] = items;
        sample["header"]!["totals"]!["recordCount"] = records;
        if (reportTitle is not null)
        {
            sample["header"]!["reportTitle"] = reportTitle;
        }

        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

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
}
