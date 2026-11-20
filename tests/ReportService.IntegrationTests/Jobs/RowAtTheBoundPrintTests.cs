extern alias orchestration;

using System.Globalization;
using System.Text;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Testing;
using ReportService.Testing.Fixtures;
using ReportService.Testing.Reading;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Common;
using JobTransitionActivities = orchestration::ReportService.Workers.Orchestration.Jobs.JobTransitionActivities;
using PdfOutputActivities = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivities;
using PdfOutputStepContracts = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputStepContracts;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The accepting side of the PDF row bound, driven as a real job through the product's own API,
/// orchestration worker and render worker on their real registrations: one PostgreSQL 18.6
/// database with the product's migrations, one Temporal 1.9.1 engine, one storage root, and the
/// sample template taken into that root.
/// <para>
/// Each at-the-bound snapshot carries a description of exactly 262 144 decoded UTF-8 bytes, and
/// the case asserts the whole accepting path as one state: the job and its PDF output succeeded,
/// the partition records no refusal, one block was printed and committed once, one artifact row
/// was published whose object verifies by size and SHA-256, and the downloaded bytes are read
/// back against the source value - the whole field contiguous, on more than one page, every page
/// carrying a word, with the neighbouring records still printed and no continuation mark. The
/// one-byte-over twins are the control that proves the boundary exact: the same shape one
/// character larger ends failed with the row-bound code, prints no page and publishes nothing.
/// </para>
/// </summary>
[Trait("Requires", "printing")]
public sealed class RowAtTheBoundPrintTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    /// <summary>The bound every refusal names, as the product counts it.</summary>
    private const int Bound = 262_144;

    /// <summary>The sample package's column captions, as a continuation page repeats them.</summary>
    private static readonly string[] PilotHeadRow = ["#", "Description", "Quantity", "Unit price", "Amount"];

    private const double PointsPerMillimetre = PlacementConvention.PointsPerMillimetre;

    private const double DescriptionColumnStartMm = 49.2;

    private const double DescriptionColumnEndMm = 86.4;

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public RowAtTheBoundPrintTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The three at-the-bound members with the characters their source value holds and the
    /// letters the reading compares: the reading uses the verification's own normalisation,
    /// which removes white space, so the matched run is the letters while the source's character
    /// count - letters and the 35 704 single spaces - is the number the acceptance names.
    /// </summary>
    public static TheoryData<string, int, int> AtTheBound => new()
    {
        { BoundFamily.Ascii, 262_144, 226_440 },
        { BoundFamily.TwoByte, 148_924, 113_220 },
        { BoundFamily.ThreeByte, 111_184, 75_480 },
    };

    /// <summary>The over-bound twins: the same shape one character larger, and its size in bytes.</summary>
    public static TheoryData<string, int> OverTheBound => new()
    {
        { BoundFamily.OverAscii, 262_145 },
        { BoundFamily.OverTwoByte, 262_146 },
        { BoundFamily.OverThreeByte, 262_147 },
    };

    [Theory]
    [MemberData(nameof(AtTheBound))]
    public async Task ARecordExactlyAtTheBoundPrintsEveryCharacterOfItsField(string fixture, int characters, int letters)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        // The source value is the family's own string: its size and character count are what the
        // construction asserts, so the verdict below is about the value the acceptance names.
        Assert.Equal(Bound, BoundFamily.Bytes(fixture));
        Assert.Equal(characters, BoundFamily.Characters(fixture));

        var snapshot = BoundFamily.Snapshot(fixture);
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(snapshot.Bytes, cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        // The job and its PDF output succeeded, and no refusal was recorded for it: the bound
        // admitted the record, which is only observable through the pass that ran.
        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal("SUCCEEDED", job.OutputStatus);
        Assert.Null(job.ErrorCode);
        Assert.Null(job.ErrorReason);
        Assert.Null(job.OutputErrorCode);
        Assert.Equal(1, job.BlockCount);
        Assert.Equal(snapshot.RecordCount, job.RecordCount);
        Assert.Matches("^artifact-[0-9a-f]{32}$", job.ArtifactId);
        Assert.StartsWith("outputs/", job.ObjectKey!, StringComparison.Ordinal);

        var partition = await rig.Jobs.FindPartitionAsync(reportJobId, cancellationToken);
        Assert.NotNull(partition);
        Assert.Null(partition!.PdfRefusalCode);
        Assert.Null(partition.PdfRefusalReason);
        Assert.DoesNotContain(
            rig.Logs.Lines,
            line => line.Contains(reportJobId, StringComparison.Ordinal)
                && line.Contains("ROW_BOUND_EXCEEDED", StringComparison.Ordinal));

        // One block was printed and committed once, and the part names the pages the artifact
        // carries: a document read back is the document that was published.
        Assert.Equal(1L, await rig.CountPartCommitsAsync(job.OutputId!, cancellationToken));
        Assert.True(job.PageCount >= 1, "a printed document takes at least one page.");
        Assert.Equal(job.PageCount, job.PartPages);
        Assert.Equal(64, job.TemplatePackageSha256!.Length);

        // The published bytes are the verified bytes: the artifact is read through the
        // verifying storage reader, and the hash of what was read is the record's. A document
        // is published only after the post-render verification passed, so the artifact's
        // presence is that verification's own result.
        var bytes = await rig.ReadArtifactAsync(job, cancellationToken);
        Assert.Equal(job.SizeBytes, bytes.LongLength);
        Assert.Equal(job.Sha256, RunEnvironment.Sha256Of(bytes));

        // The printed field is read back against the source, never against the layout engine's
        // own output: the whole value appears once, contiguous, across more than one page.
        var path = WriteEvidence(fixture + ".job.pdf", bytes);
        try
        {
            var reading = PrintedFieldText.Read(
                path,
                15 * PointsPerMillimetre,
                12 * PointsPerMillimetre,
                18 * PointsPerMillimetre,
                12 * PointsPerMillimetre,
                PilotHeadRow,
                DescriptionColumnStartMm * PointsPerMillimetre,
                DescriptionColumnEndMm * PointsPerMillimetre);
            var match = reading.Find(BoundFamily.Description(fixture));

            Assert.True(match.Passed, $"{fixture}: {match.Difference} at {match.Position} (page {match.Page}).");
            Assert.Equal(letters, match.Length);
            Assert.True(match.LastPage > match.FirstPage, $"{fixture}: the row printed on one page.");
            Assert.Equal(job.PageCount!.Value, reading.Pages.Count);
            Assert.True(reading.Pages.All(page => page.Words.Count > 0), $"{fixture}: a page printed no word.");
            Assert.DoesNotContain("(continues)", reading.Text, StringComparison.Ordinal);

            // The records on either side of the bound record are printed too: the block around
            // the fragmented row is intact.
            Assert.True(reading.Find("ordinary one").Passed, $"{fixture}: the record before the bound record is missing.");
            Assert.True(reading.Find("ordinary three").Passed, $"{fixture}: the record after the bound record is missing.");

            // The document was printed on the path without page accounting, so it carries no
            // running subtotal, brought-forward or carried-forward text of that path.
            AssertNoCarriedValue(reading);

            // The output child ran - the print and the publication are its work, so its start is
            // what the job's own history shows about them - and one block was committed above.
            var history = await rig.HistoryAsync(reportJobId, cancellationToken);
            Assert.Contains(history.Events, entry => entry.EventType == EventType.StartChildWorkflowExecutionInitiated);

            WriteReadings(fixture, snapshot, job, match, letters, partition);
            AssertNoPartOfTheValueWasStoredOrLogged(rig, reportJobId, job, BoundFamily.Description(fixture));
        }
        finally
        {
            if (RunEnvironment.RunDirectory is null)
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AnAsciiRecordOneByteOverTheBoundIsRefusedBeforeAnyPageIsRendered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        await AssertRefusedAsync(rig, BoundFamily.OverAscii, 262_145, cancellationToken);
    }

    [Theory]
    [MemberData(nameof(OverTheBoundMultiByte))]
    public async Task AMultiByteRecordOneCharacterOverTheBoundIsRefusedBeforeAnyPageIsRendered(string fixture, int size)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        await AssertRefusedAsync(rig, fixture, size, cancellationToken);
    }

    /// <summary>The two multi-byte twins: one character over is over the bound, because the unit is bytes.</summary>
    public static TheoryData<string, int> OverTheBoundMultiByte => new()
    {
        { BoundFamily.OverTwoByte, 262_146 },
        { BoundFamily.OverThreeByte, 262_147 },
    };

    // One refused snapshot, asserted whole: the job and its PDF output failed with the same code
    // and the same stored reason naming the record, the field, its size and the bound, the
    // partition holds the refusal, no page was rendered and nothing a consumer could reach was
    // written, and no part of the value was stored or logged.
    private static async Task AssertRefusedAsync(
        PrintingRig rig,
        string fixture,
        int size,
        CancellationToken cancellationToken)
    {
        Assert.Equal(size, BoundFamily.Bytes(fixture));

        var value = BoundFamily.Description(fixture);
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(BoundFamily.Snapshot(fixture).Bytes, cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("FAILED", job.Status);
        Assert.Equal("ROW_BOUND_EXCEEDED", job.ErrorCode);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal("ROW_BOUND_EXCEEDED", job.OutputErrorCode);
        Assert.Equal(job.ErrorReason, job.OutputErrorReason);

        // The bound record is the second of the snapshot's three records, and the reason names
        // it: the record's number, the field's path, its size and the bound, never a value.
        var reason = job.ErrorReason;
        Assert.NotNull(reason);
        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size={size} bound={Bound} limit=row-bound ordinal=2 offset=");
        Assert.StartsWith(prefix, reason, StringComparison.Ordinal);
        var closing = reason![prefix.Length..];
        Assert.EndsWith(" path=$.description", closing, StringComparison.Ordinal);
        var offset = closing[..^" path=$.description".Length];
        Assert.True(offset.Length > 0 && offset.All(char.IsAsciiDigit), $"the reason closes with an offset, and '{offset}' is not one");

        var partition = await rig.Jobs.FindPartitionAsync(reportJobId, cancellationToken);
        Assert.NotNull(partition);
        Assert.Equal("ROW_BOUND_EXCEEDED", partition!.PdfRefusalCode);
        Assert.Equal(reason, partition.PdfRefusalReason);
        Assert.True(
            Directory.Exists(Path.Combine(rig.StorageRoot, "block", reportJobId, "0")),
            "the block of the refused partition is an intermediate object and it exists");

        // Nothing was rendered for the output and nothing was published.
        Assert.Equal(0L, await rig.CountPartCommitsAsync(job.OutputId!, cancellationToken));
        Assert.Null(await rig.Jobs.FindPartAsync(job.OutputId!, 0, cancellationToken));
        var artifacts = await rig.Jobs.ListArtifactsAsync(reportJobId, PrintingRig.Consumer, cancellationToken);
        Assert.True(artifacts is null || artifacts.Artifacts.Count == 0, "a refused job publishes no artifact");
        Assert.Null(job.ArtifactId);
        Assert.False(
            Directory.Exists(Path.Combine(rig.StorageRoot, "outputs", reportJobId)),
            "a refused job wrote an object under the output namespace");

        // The history shows the refusal the job was ended with, and no output child: no print
        // and no publication can have run.
        var history = await rig.HistoryAsync(reportJobId, cancellationToken);
        Assert.DoesNotContain(history.Events, entry => entry.EventType == EventType.StartChildWorkflowExecutionInitiated);
        Assert.Empty(Activities(history, PdfOutputStepContracts.RenderPdfBlock));
        Assert.Empty(Activities(history, PdfOutputActivities.PublishOutputName));
        Assert.Single(Activities(history, JobTransitionActivities.FailOutputName));

        AssertNoPartOfTheValueWasStoredOrLogged(rig, reportJobId, job, value);
    }

    // The value never reaches the service's own words: every sixteen characters of it are looked
    // for in both stored reasons and in every line the run logged for the job.
    private static void AssertNoPartOfTheValueWasStoredOrLogged(
        PrintingRig rig,
        string reportJobId,
        JobStateRow job,
        string value)
    {
        AssertNoWindow(window => Assert.DoesNotContain(window, job.ErrorReason ?? string.Empty, StringComparison.Ordinal), value, "the job's stored reason");
        AssertNoWindow(window => Assert.DoesNotContain(window, job.ErrorMessage ?? string.Empty, StringComparison.Ordinal), value, "the job's stored message");
        AssertNoWindow(window => Assert.DoesNotContain(window, job.OutputErrorReason ?? string.Empty, StringComparison.Ordinal), value, "the output's stored reason");
        foreach (var line in rig.Logs.Lines.Where(line => line.Contains(reportJobId, StringComparison.Ordinal)))
        {
            AssertNoWindow(window => Assert.DoesNotContain(window, line, StringComparison.Ordinal), value, "a log line of the job");
        }
    }

    private static void AssertNoWindow(Action<string> assert, string text, string where)
    {
        for (var start = 0; start + 16 <= text.Length; start += 8)
        {
            assert(text.Substring(start, 16));
        }
    }

    private static IReadOnlyList<HistoryEvent> Activities(WorkflowHistory history, string name) =>
        [.. history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Where(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                name,
                StringComparison.Ordinal))];

    // The path prints no page accounting: no running subtotal and no carried value appears in
    // the document's own words.
    private static void AssertNoCarriedValue(PrintedFieldReading reading)
    {
        var printed = string.Join(" ", reading.Pages.SelectMany(page => page.Words).Select(word => word.Text));
        Assert.DoesNotContain("subtotal", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("brought forward", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("carried forward", printed, StringComparison.OrdinalIgnoreCase);
    }

    // The printed document stays with the run when the runner named one, so the acceptance can
    // read the very file this case measured; a class run by hand writes into the temporary
    // directory instead.
    private static string WriteEvidence(string fileName, byte[] content)
    {
        if (RunEnvironment.RunDirectory is not null)
        {
            return RunEnvironment.WriteEvidence(fileName, content);
        }

        var path = Path.Combine(Path.GetTempPath(), "report-service-bound-" + Guid.NewGuid().ToString("N") + "-" + fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void WriteReadings(string fixture, SnapshotFixture snapshot, JobStateRow job, FieldMatch match, int letters, PartitionRecord partition)
    {
        if (RunEnvironment.RunDirectory is null)
        {
            return;
        }

        var text = new StringBuilder()
            .AppendLine($"fixture={fixture}")
            .AppendLine($"sourceSha256={BoundFamily.SourceSha256(fixture)}")
            .AppendLine($"snapshotSha256={snapshot.Sha256}")
            .AppendLine($"artifact={job.ArtifactId}")
            .AppendLine($"pages={job.PageCount}")
            .AppendLine($"sizeBytes={job.SizeBytes}")
            .AppendLine($"sha256={job.Sha256}")
            .AppendLine($"templatePackageSha256={job.TemplatePackageSha256}")
            .AppendLine($"blockCount={job.BlockCount}")
            .AppendLine($"recordCount={job.RecordCount}")
            .AppendLine($"pdfRefusal={partition.PdfRefusalCode ?? "none"}")
            .AppendLine($"matched={match.Length}")
            .AppendLine($"letters={letters}")
            .AppendLine($"firstPage={match.FirstPage}")
            .AppendLine($"lastPage={match.LastPage}")
            .ToString();
        RunEnvironment.WriteEvidence(fixture + ".job.txt", Encoding.UTF8.GetBytes(text));
    }
}
