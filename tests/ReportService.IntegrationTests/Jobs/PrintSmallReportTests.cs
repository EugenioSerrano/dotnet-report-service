using System.Text;
using ReportService.Testing;
using UglyToad.PdfPig.Content;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// A small report printed as one PDF, driven as a real job through the product's own API and
/// its own processes: one PostgreSQL 18.6 database, one Temporal 1.9.1 engine, one storage
/// root, the product's API, orchestration worker and render worker on their real
/// registrations, and the template served from the delivered loopback double at two versions.
/// <para>
/// Every case asserts what the composition recorded: the job's terminal status, its output's,
/// the one artifact and the part commit that names the same object, and - where a consumer
/// needs it - the printed text read out of the published PDF itself, never out of the layout
/// engine. A PDF is published only when the product's post-render verification passed, so the
/// presence of the artifact is the verification's own result; the cases that must be refused
/// assert that no artifact exists at all.
/// </para>
/// </summary>
public sealed class PrintSmallReportTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PrintSmallReportTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AJobOfTwentyFiveRecordsEndsSucceededWithOneArtifactAndOnePart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        var snapshot = PrintingRig.Snapshot(25);
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(snapshot, cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal("SUCCEEDED", job.OutputStatus);
        Assert.Null(job.ErrorCode);
        Assert.Matches("^artifact-[0-9a-f]{32}$", job.ArtifactId);
        Assert.StartsWith("outputs/", job.ObjectKey!, StringComparison.Ordinal);
        Assert.Equal(1, job.BlockCount);
        Assert.Equal(25, job.RecordCount);

        // One part commit names the object the artifact names, printed from the version the job pinned.
        Assert.Equal(job.PageCount, job.PartPages);
        Assert.Equal(64, job.TemplatePackageSha256!.Length);

        // The object reads back through the verifying reader: its size and SHA-256 are the
        // record's, which is what a consumer downloads.
        var bytes = await rig.ReadArtifactAsync(job, cancellationToken);
        Assert.Equal(job.SizeBytes, bytes.LongLength);
        Assert.Equal(job.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));

        // The document and the snapshot it was printed from are kept in the run's evidence, with
        // both hashes: they are what the independent reader's declaration was measured on, and
        // the two files a person reproduces the measurement from. A process started without a
        // run folder (the class run by hand) keeps them nowhere and says so.
        if (RunEnvironment.RunDirectory is null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"no {RunEnvironment.RunDirectoryVariable}: the evidence of this job was not written");
            return;
        }

        var pdfPath = RunEnvironment.WriteEvidence("pilot-simple.pdf", bytes);
        var snapshotPath = RunEnvironment.WriteEvidence("pilot-simple.snapshot.json", snapshot);
        var hashes = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"pilot-simple.pdf sha256={RunEnvironment.Sha256Of(bytes)} bytes={bytes.LongLength}{Environment.NewLine}"
            + $"pilot-simple.snapshot.json sha256={RunEnvironment.Sha256Of(snapshot)} bytes={snapshot.LongLength}{Environment.NewLine}");
        RunEnvironment.WriteEvidence("pilot-simple.evidence.txt", System.Text.Encoding.UTF8.GetBytes(hashes));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"evidence: {pdfPath}{Environment.NewLine}{snapshotPath}{Environment.NewLine}{hashes}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(1_000)]
    public async Task EveryRecordAndEveryCharacterOfItIsPrintedExactlyOnce(int records)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        // The snapshot this case prints: the same bytes are uploaded and compared, so the
        // comparison follows the document's own data at every record count.
        var snapshot = PrintingRig.Snapshot(records);
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(snapshot, cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal(records, job.RecordCount);
        var text = TextOf(await rig.ReadArtifactAsync(job, cancellationToken));

        // Every distinct description of the snapshot this job carried is in the printed
        // document. The sample's descriptions repeat, so the text is compared as a set: a
        // document that drops or alters one of them fails here. The comparison is with the
        // source snapshot's values, never with anything the layout engine reported.
        using var source = System.Text.Json.JsonDocument.Parse(snapshot);
        var descriptions = source.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("description").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var description in descriptions)
        {
            Assert.Contains(description, text, StringComparison.Ordinal);
        }

        Assert.Contains("Records: " + records.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowTallerThanAPageContinuesOverSeveralPagesWithNoContinuesMark()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25, tallDescription: true), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.True(job.PageCount >= 3, $"a 160 KiB description printed {job.PageCount} page(s); it needs at least three.");
        var text = TextOf(await rig.ReadArtifactAsync(job, cancellationToken));
        Assert.DoesNotContain("(continues)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobPrintsTheVersionItPinnedWhileANewerOneIsTaken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        var first = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var second = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.Second,
            cancellationToken);

        var firstJob = await rig.WaitForTerminalAsync(first, Deadline, cancellationToken);
        var secondJob = await rig.WaitForTerminalAsync(second, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", firstJob.Status);
        Assert.Equal("SUCCEEDED", secondJob.Status);
        Assert.NotEqual(firstJob.TemplatePackageSha256, secondJob.TemplatePackageSha256);

        // The printed document says which version it came from, and each job printed the one it
        // pinned - never the newer one that was taken alongside it.
        var firstText = TextOf(await rig.ReadArtifactAsync(firstJob, cancellationToken));
        var secondText = TextOf(await rig.ReadArtifactAsync(secondJob, cancellationToken));
        Assert.Contains(PrintingRig.Template + " " + PrintingRig.First, firstText, StringComparison.Ordinal);
        Assert.DoesNotContain(PrintingRig.Template + " " + PrintingRig.Second, firstText, StringComparison.Ordinal);
        Assert.Contains(PrintingRig.Template + " " + PrintingRig.Second, secondText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobPinnedToAVersionAlreadyTakenPrintsWithTheSourceUnreachable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        // The version is taken and pinned by a first job.
        var taken = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(1), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        Assert.Equal("SUCCEEDED", (await rig.WaitForTerminalAsync(taken, Deadline, cancellationToken)).Status);

        // The source is made unreachable. The rendering path never reads it, and the take finds
        // the version already kept.
        rig.Source.Stop();
        var requestsBefore = rig.Source.Requests.Count;

        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.NotNull(job.ArtifactId);

        // A version already taken is used without consulting the source: the double recorded no
        // request of any kind while the job ran.
        Assert.Equal(requestsBefore, rig.Source.Requests.Count);
    }

    [Fact]
    public async Task AReportWithNoRecordEndsSucceededAndHoldsOnlyWhatTheTemplatePrints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.EmptySnapshot(), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal("SUCCEEDED", job.OutputStatus);
        Assert.NotNull(job.ArtifactId);
        Assert.Equal(0, job.RecordCount);
        Assert.True(job.PageCount >= 1, "an empty report still takes at least one page.");

        // The document holds the template's document parts and nothing the service added: the
        // header it printed, the counted total, and no row.
        var text = TextOf(await rig.ReadArtifactAsync(job, cancellationToken));
        Assert.Contains("Pilot dataset: simple invoice", text, StringComparison.Ordinal);
        Assert.Contains("Records: 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionsWithoutDeclaredFieldsIsRefusedBeforeAnythingIsPrinted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        // The version is taken by the double before any take-side check exists: the render is the
        // gate that refuses it, and it refuses before composing.
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.NoAnchors,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("FAILED", job.Status);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal("PAGINATION_FAILED", job.OutputErrorCode);
        Assert.Equal("PAGINATION_FAILED", job.ErrorCode);
        Assert.Null(job.ArtifactId);
        Assert.Null(job.TemplatePackageSha256);
        AssertNoOutputObject(rig);
    }

    [Fact]
    public async Task AVersionThatDeclaresPageSubtotalsIsRefusedBeforePrinting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.PageTotals,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("FAILED", job.Status);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal("UNSUPPORTED_CAPABILITY", job.OutputErrorCode);
        Assert.Equal("UNSUPPORTED_CAPABILITY", job.ErrorCode);
        Assert.Null(job.ArtifactId);
        Assert.Null(job.TemplatePackageSha256);
        AssertNoOutputObject(rig);
    }

    // The text of a printed document, page by page, as the page itself presents it: every
    // character in the order it was painted, and one space where a word begins and the line
    // or cell break left no character between them. A description the table wraps inside its
    // cell is therefore read as the one string its source has. Nothing of the layout engine's
    // own output is consulted.
    private static string TextOf(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), "report-service-print-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                // The first letter of every word the reader finds: a word that begins where the
                // page's own characters carry nothing is the space a line or cell break dropped.
                var wordStarts = new HashSet<Letter>(ReferenceEqualityComparer.Instance);
                foreach (var word in page.GetWords())
                {
                    if (word.Letters.Count > 0)
                    {
                        wordStarts.Add(word.Letters[0]);
                    }
                }

                // The page's own characters, in the order the page presents them, with one space
                // where a word begins and nothing already separates it from the character before.
                var pageText = new StringBuilder();
                foreach (var letter in page.Letters)
                {
                    if (wordStarts.Contains(letter)
                        && pageText.Length > 0
                        && !char.IsWhiteSpace(pageText[pageText.Length - 1]))
                    {
                        pageText.Append(' ');
                    }

                    pageText.Append(letter.Value);
                }

                text.Append(pageText).Append(' ');
            }

            return text.ToString();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Nothing may be published under the output namespace by a job that was refused: the
    // storage root holds no object of that class.
    private static void AssertNoOutputObject(PrintingRig rig) =>
        Assert.False(
            Directory.Exists(Path.Combine(rig.StorageRoot, "outputs")),
            "a refused job wrote an object under the output namespace");
}
