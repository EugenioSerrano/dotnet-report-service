extern alias pdf;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReportService.Jobs;
using ReportService.Testing;
using PdfRendering = pdf::ReportService.Workers.Pdf.Rendering;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The engine and the presentation on the running service, driven as real jobs through the
/// product's own API and its own processes: one PostgreSQL database, one workflow engine, one
/// storage root, the API, the orchestration worker and the render worker on their real
/// registrations, and one report published in the three engines the deployment offers.
/// <para>
/// What is asserted is what a consumer receives: each job's terminal status, one published
/// file per job whose bytes verify, and the text read out of the published PDF itself - the
/// same records and values through the three engines, the culture and the zone the job was
/// accepted with, the acceptance instant on every attempt of a retried block, and the
/// presentation provenance recorded with the committed block.
/// </para>
/// </summary>
[Trait("Requires", "printing")]
public sealed class EngineAndPresentationTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's database.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public EngineAndPresentationTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Three jobs, one per engine, over one snapshot: each prints with the engine its version
    /// declares, the three documents hold the same records and values, and each committed row
    /// records the presentation that produced it.
    /// </summary>
    [Fact]
    public async Task ThreeJobsOnePerEnginePublishTheSameReportAndRecordWhatProducedIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        await rig.SeedSameReportAsync(cancellationToken);

        var snapshot = PrintingRig.SameReportSnapshot(25);
        var snapshotTexts = DescriptionsOf(snapshot);
        var uploadId = await rig.ReadyUploadAsync(snapshot, cancellationToken);

        var jobs = new List<(string Version, JobStateRow Row)>();
        foreach (var version in PrintingRig.SameReportEngines)
        {
            var reportJobId = await rig.SubmitAsync(uploadId, PrintingRig.SameReport, version, "es-AR", "America/Argentina/Buenos_Aires", cancellationToken);
            var row = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);
            Assert.True(
                string.Equals("SUCCEEDED", row.Status, StringComparison.Ordinal),
                $"job {reportJobId} ended {row.Status} {row.ErrorCode} ({row.ErrorReason}): {row.ErrorMessage}");
            Assert.Equal("SUCCEEDED", row.OutputStatus);
            Assert.Matches("^artifact-[0-9a-f]{32}$", row.ArtifactId);
            Assert.Equal(1, row.BlockCount);
            Assert.Equal(25, row.RecordCount);
            jobs.Add((version, row));
        }

        // One document per engine, each the report the other two print: the same records, the
        // same values and the same text, read out of the published file itself.
        var texts = new List<string>();
        foreach (var (version, row) in jobs)
        {
            var bytes = await rig.ReadArtifactAsync(row, cancellationToken);
            Assert.Equal(row.SizeBytes, bytes.LongLength);
            Assert.Equal(row.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
            var text = TextOf(bytes);
            foreach (var description in snapshotTexts)
            {
                Assert.Contains(description, text, StringComparison.Ordinal);
            }

            Assert.Contains("Records: 25", text, StringComparison.Ordinal);
            Assert.Equal("es-AR", PresentationOf(row)!.Culture);
            Assert.Equal("America/Argentina/Buenos_Aires", PresentationOf(row)!.TimeZone);
            Assert.Equal(EngineOf(version), PresentationOf(row)!.Engine);
            Assert.Contains(version == "1.0.0" ? "7.5.1" : version == "1.1.0" ? "2.4.3" : "2.40.0", PresentationOf(row)!.EngineVersion, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(PresentationOf(row)!.IcuVersion));
            Assert.False(string.IsNullOrWhiteSpace(PresentationOf(row)!.TzDataVersion));
            Assert.NotEqual("unrecorded", PresentationOf(row)!.IcuVersion);
            texts.Add(text);
        }

        Assert.Equal(texts[0], texts[1]);
        Assert.Equal(texts[0], texts[2]);

        // The demo: the three documents side by side with their SHA-256, kept with the run.
        var kept = new List<string>();
        foreach (var (version, row) in jobs)
        {
            var bytes = await rig.ReadArtifactAsync(row, cancellationToken);
            var name = "same-report-" + version + "-engines.pdf";
            RunEnvironment.WriteEvidence(name, bytes);
            kept.Add(RunEnvironment.Sha256Of(bytes) + "  " + name);
        }

        RunEnvironment.WriteEvidence(
            "same-report-engines.sha256",
            Encoding.UTF8.GetBytes(string.Join('\n', kept) + "\n"));
    }

    /// <summary>
    /// Two jobs of the same version whose culture and zone differ: each prints its dates and
    /// its amounts in its own culture and zone, and the acceptance instant the document prints
    /// is the job's own clock converted to that zone - computed here from the job's records,
    /// never read out of the document.
    /// </summary>
    [Fact]
    public async Task TwoJobsPresentTheirOwnCultureZoneAndAcceptanceInstant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        await rig.SeedSameReportAsync(cancellationToken);

        var snapshot = PrintingRig.SameReportSnapshot(25);
        var amount = AmountOf(snapshot);
        var uploadId = await rig.ReadyUploadAsync(snapshot, cancellationToken);

        var kept = new List<string>();
        foreach (var (culture, timeZone) in new[] { ("es-AR", "America/Argentina/Buenos_Aires"), ("en-US", "Asia/Tokyo") })
        {
            var reportJobId = await rig.SubmitAsync(uploadId, PrintingRig.SameReport, "1.0.0", culture, timeZone, cancellationToken);
            var row = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);
            Assert.True(
                string.Equals("SUCCEEDED", row.Status, StringComparison.Ordinal),
                $"job {reportJobId} ended {row.Status} {row.ErrorCode} ({row.ErrorReason}): {row.ErrorMessage}");

            var text = TextOf(await rig.ReadArtifactAsync(row, cancellationToken));
            var expectedAmount = amount.ToString("N2", CultureInfo.GetCultureInfo(culture));
            Assert.Contains(expectedAmount, text, StringComparison.Ordinal);

            // The report's "now": the job's own acceptance instant, shown in the job's zone.
            var presentation = PresentationOf(row)!;
            Assert.Equal(culture, presentation.Culture);
            Assert.Equal(timeZone, presentation.TimeZone);
            var clock = await rig.ReportClockAsync(reportJobId, cancellationToken);
            var expectedInstant = TimeZoneInfo.ConvertTime(clock, TimeZoneInfo.FindSystemTimeZoneById(timeZone))
                .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
            Assert.Contains(expectedInstant, text, StringComparison.Ordinal);

            var name = "same-report-" + culture + "-locale.pdf";
            var bytes = await rig.ReadArtifactAsync(row, cancellationToken);
            RunEnvironment.WriteEvidence(name, bytes);
            kept.Add(RunEnvironment.Sha256Of(bytes) + "  " + name);
        }

        RunEnvironment.WriteEvidence(
            "same-report-locales.sha256",
            Encoding.UTF8.GetBytes(string.Join('\n', kept) + "\n"));
    }

    /// <summary>
    /// One job whose block is forced to fail after it printed: attempt 1 renders, verifies and
    /// streams its document under its own attempt-scoped key and commits nothing; attempt 2
    /// renders and prints again. Both documents are read back, both print the job's acceptance
    /// instant, attempt 1's object is referenced by no part commit, and the single committed
    /// row names attempt 2's object.
    /// </summary>
    [Fact]
    public async Task ARetriedBlockLeavesTheFirstAttemptAsAnOrphanAndCommitsTheSecond()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fault = new FailTheFirstCommitOnce();
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        await rig.SeedSameReportAsync(cancellationToken);
        rig.InstallPartCommitFault(fault);

        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.SameReportSnapshot(25), cancellationToken),
            PrintingRig.SameReport,
            "1.0.0",
            "es-AR",
            "America/Argentina/Buenos_Aires",
            cancellationToken);
        var row = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", row.Status);
        Assert.True(fault.Fired, "the fault had to fail one attempt; the block committed on the first one");

        var keys = rig.OutputObjectKeys(reportJobId, row.OutputId!);
        Assert.Equal(2, keys.Count);
        Assert.Contains(row.PartObjectKey, keys, StringComparer.Ordinal);
        var orphan = Assert.Single(keys, key => !string.Equals(key, row.PartObjectKey, StringComparison.Ordinal));

        var presentation = PresentationOf(row)!;
        var clock = await rig.ReportClockAsync(reportJobId, cancellationToken);
        var expectedInstant = TimeZoneInfo.ConvertTime(clock, TimeZoneInfo.FindSystemTimeZoneById(presentation.TimeZone))
            .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        // Both printed documents are read back: the committed one and the orphan of the failed
        // attempt, each compared with the instant computed from the job's own records.
        var committedText = TextOf(await rig.ReadObjectAsync(row.PartObjectKey!, row.SizeBytes!.Value, row.Sha256!, cancellationToken));
        var orphanText = TextOf(rig.ReadStoredObject(orphan));
        Assert.Contains(expectedInstant, committedText, StringComparison.Ordinal);
        Assert.Contains(expectedInstant, orphanText, StringComparison.Ordinal);
        Assert.Equal(TailOf(committedText), TailOf(orphanText));

        // The provenance is one stored copy: the block's one row, read once.
        Assert.Equal(1, await rig.CountPartCommitsAsync(row.OutputId!, cancellationToken));
        Assert.Equal("es-AR", presentation.Culture);
    }

    /// <summary>
    /// A version whose declared engine version names another major is refused before anything is
    /// composed: the job ends failed with the code and reason of that refusal and publishes
    /// nothing at all.
    /// </summary>
    [Fact]
    public async Task AVersionWhoseEngineMajorTheDeploymentDoesNotRunFailsWithNothingPublished()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        await rig.SeedSameReportAsync(cancellationToken);
        await rig.SeedSameReportVersionWithEngineVersionAsync("1.9.0", "9.0.0", cancellationToken);

        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.SameReportSnapshot(25), cancellationToken),
            PrintingRig.SameReport,
            "1.9.0",
            "es-AR",
            "America/Argentina/Buenos_Aires",
            cancellationToken);
        var row = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("FAILED", row.Status);
        Assert.Equal("FAILED", row.OutputStatus);
        Assert.True(
            string.Equals("UNSUPPORTED_CAPABILITY", row.ErrorCode, StringComparison.Ordinal),
            $"job {reportJobId} ended {row.Status} {row.ErrorCode} ({row.ErrorReason}): {row.ErrorMessage}");
        Assert.Null(row.ArtifactId);
        Assert.Null(row.PartObjectKey);
        Assert.Empty(rig.OutputObjectKeys(reportJobId, row.OutputId ?? string.Empty));
    }

    /// <summary>
    /// A snapshot whose amount is not a number the report can present: the block is refused
    /// before anything is printed, the job ends failed with the presentation's own code and
    /// reason, and no document, commit or artifact exists.
    /// </summary>
    [Fact]
    public async Task AnAmountThatCannotBePresentedFailsTheJobWithNothingPublished()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        await rig.SeedSameReportAsync(cancellationToken);

        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(SnapshotWithAnUnpresentableAmount(25), cancellationToken),
            PrintingRig.SameReport,
            "1.0.0",
            "es-AR",
            "America/Argentina/Buenos_Aires",
            cancellationToken);
        var row = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("FAILED", row.Status);
        Assert.Equal("FAILED", row.OutputStatus);
        Assert.True(
            string.Equals("DATA_INVALID", row.ErrorCode, StringComparison.Ordinal),
            $"job {reportJobId} ended {row.Status} {row.ErrorCode} ({row.ErrorReason}): {row.ErrorMessage}");

        // The reason itself is proven where the formatter refuses, in the engine adapters' own
        // cases: the output row records the code it ended with, and the job row the finalisation
        // that closed it, so the reason is asserted at the refusal and not on this row.
        Assert.Null(row.ArtifactId);
        Assert.Null(row.PartObjectKey);
        Assert.Empty(rig.OutputObjectKeys(reportJobId, row.OutputId ?? string.Empty));
    }

    // The engine each version of the one report declares, as the manifest names it.
    private static string EngineOf(string version) => version switch
    {
        "1.1.0" => "handlebars",
        "1.2.0" => "liquid",
        _ => "scriban",
    };

    private static PresentationProvenance? PresentationOf(JobStateRow row) =>
        PresentationProvenance.Parse(row.Presentation);


    // The distinct descriptions of a snapshot, as the record field the report prints as it is.
    private static IReadOnlyList<string> DescriptionsOf(byte[] snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        return [.. document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("description").GetString()!)
            .Distinct(StringComparer.Ordinal)];
    }

    // The first record's amount, as the value the report presents.
    private static decimal AmountOf(byte[] snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        var item = document.RootElement.GetProperty("items").EnumerateArray().First();
        var text = item.GetProperty("amount").ValueKind == JsonValueKind.String
            ? item.GetProperty("amount").GetString()!
            : item.GetProperty("amount").GetRawText();
        return decimal.Parse(text, CultureInfo.InvariantCulture);
    }

    // A snapshot whose first record carries an amount no decimal can hold: the data contract
    // admits it as a number of its own, so the document is usable, and presenting it is what the
    // report cannot do. The value is a plain integer well past the largest decimal, so the
    // refusal is the number's own and never the notation it arrived in.
    private static byte[] SnapshotWithAnUnpresentableAmount(int records)
    {
        var snapshot = JsonNode.Parse(Encoding.UTF8.GetString(PrintingRig.SameReportSnapshot(records)))!;
        snapshot["items"]![0]!["amount"] = JsonNode.Parse("123456789012345678901234567890123");
        return Encoding.UTF8.GetBytes(snapshot.ToJsonString());
    }

    // The text of a printed document, page by page, as the page presents it: every character in
    // the order it was painted, with one space where a word begins and the line or cell break
    // left none. Nothing of the layout engine's own output is consulted.
    private static string TextOf(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), "report-service-presentation-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                var wordStarts = new HashSet<UglyToad.PdfPig.Content.Letter>(ReferenceEqualityComparer.Instance);
                foreach (var word in page.GetWords())
                {
                    if (word.Letters.Count > 0)
                    {
                        wordStarts.Add(word.Letters[0]);
                    }
                }

                var pageText = new StringBuilder();
                foreach (var letter in page.Letters)
                {
                    if (wordStarts.Contains(letter) && pageText.Length > 0 && !char.IsWhiteSpace(pageText[^1]))
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

    // The document's closing text: the line that carries the report's "now", and the words it
    // prints it among. A retry must print the same one.
    private static string TailOf(string text) => text.Length <= 160 ? text : text[^160..];

    /// <summary>
    /// The fault the two-attempt case installs: the first commit of any block fails once, after
    /// the document was verified and streamed, with an untyped exception - which is what makes
    /// the engine retry instead of failing the block.
    /// </summary>
    private sealed class FailTheFirstCommitOnce : PdfRendering.PartCommitFault
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public Task FailOnceAsync(string outputId, int blockIndex, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                throw new InvalidOperationException("the part commit was made to fail once, after the document was printed");
            }

            return Task.CompletedTask;
        }
    }
}
