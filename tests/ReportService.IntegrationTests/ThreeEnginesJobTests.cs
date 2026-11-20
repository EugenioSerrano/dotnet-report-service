using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReportService.Jobs;
using ReportService.Testing;
using UglyToad.PdfPig;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The takeable family on the running service: three versions of one report, one engine
/// each, published into the deployment's own template source and taken by the job that
/// names them - not seeded taken. A job per version ends <c>succeeded</c>, its published
/// document carries the snapshot's records and values, and the three documents print the
/// same report. A version whose sample does not render is published the same way and the
/// job that names it ends <c>failed</c> with <c>TEMPLATE_INVALID</c>, publishing nothing.
/// </summary>
[Trait("Requires", "printing")]
public sealed class ThreeEnginesJobTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's database.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public ThreeEnginesJobTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AJobOnEachTakeableVersionSucceedsAndTheThreePrintTheSameReport()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        foreach (var version in PrintingRig.SampleGateEngines)
        {
            await rig.PublishSampleGateAsync(version, cancellationToken);
        }

        var snapshot = PrintingRig.SameReportSnapshot(25);
        var descriptions = DescriptionsOf(snapshot);
        var uploadId = await rig.ReadyUploadAsync(snapshot, cancellationToken);

        var texts = new List<string>();
        foreach (var version in PrintingRig.SampleGateEngines)
        {
            var jobId = await rig.SubmitAsync(
                uploadId,
                PrintingRig.SampleGate,
                version,
                "es-AR",
                "America/Argentina/Buenos_Aires",
                cancellationToken);
            var row = await rig.WaitForTerminalAsync(jobId, Deadline, cancellationToken);
            Assert.True(
                string.Equals("SUCCEEDED", row.Status, StringComparison.Ordinal),
                $"job {jobId} on {version} ended {row.Status} {row.ErrorCode} ({row.ErrorReason}): {row.ErrorMessage}");
            Assert.Equal("SUCCEEDED", row.OutputStatus);
            Assert.Equal(25, row.RecordCount);

            var bytes = await rig.ReadArtifactAsync(row, cancellationToken);
            var text = TextOf(bytes);
            foreach (var description in descriptions)
            {
                Assert.Contains(description, text, StringComparison.Ordinal);
            }

            Assert.Contains("Records: 25", text, StringComparison.Ordinal);
            texts.Add(Normalise(text));
        }

        Assert.Equal(texts[0], texts[1]);
        Assert.Equal(texts[0], texts[2]);
    }

    [Fact]
    public async Task AJobOnAVersionWhoseSampleDoesNotRenderFailsAndPublishesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        await rig.PublishSampleGateVariantAsync(
            "1.0.0",
            "1.9.0",
            files => files["html/row.html.sbn"] = "<tr><td></td></tr>"u8.ToArray(),
            cancellationToken);

        var uploadId = await rig.ReadyUploadAsync(PrintingRig.SameReportSnapshot(3), cancellationToken);
        var jobId = await rig.SubmitAsync(
            uploadId,
            PrintingRig.SampleGate,
            "1.9.0",
            "es-AR",
            "America/Argentina/Buenos_Aires",
            cancellationToken);

        var row = await rig.WaitForTerminalAsync(jobId, Deadline, cancellationToken);
        Assert.Equal("FAILED", row.Status);
        Assert.Equal("TEMPLATE_INVALID", row.ErrorCode);
        Assert.Contains("row-without-text", row.ErrorMessage ?? row.ErrorReason ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(row.ArtifactId);
    }

    private static IReadOnlyList<string> DescriptionsOf(byte[] snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        return [.. document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("description").GetString()!)
            .Distinct(StringComparer.Ordinal)];
    }

    private static string TextOf(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), "report-service-three-engines-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            using var document = PdfDocument.Open(path);
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                _ = text.Append(page.Text).Append('\n');
            }

            return text.ToString();
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    // The only field the three documents do not share is the footer's acceptance minute,
    // which each job prints from its own clock: it is normalised out before the comparison.
    private static readonly Regex AcceptanceMinute =
        new(@"\d{2}/\d{2}/\d{4} \d{2}:\d{2}", RegexOptions.CultureInvariant);

    private static string Normalise(string text) =>
        AcceptanceMinute.Replace(text, "<acceptance minute>");
}
