using System.Globalization;
using System.Text;
using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.EndToEndTests;

/// <summary>
/// A template that declares a per-page code, over the whole local composition: the
/// composition enables the product's library-free reference generator in both the
/// render worker (which takes the template and proves the code on its sample) and the
/// merge worker (which draws it). A multi-block job and a one-block job each publish
/// one PDF in which every page carries exactly one code object, inside the declared
/// area, whose content is the reference generator's content for the value the
/// declared source gives on that page - computed here from the template's identity,
/// the page's position and the running header every page prints, never read back
/// from the code. The multi-block document keeps its "X of Y" stamp; the one-block
/// document keeps its own page counter and gains no stamp. The merge worker reports the
/// code figures it records as metrics (generator, pages, bytes, time, failures) for each.
/// </summary>
[Trait("Requires", "printing")]
public sealed class PageCodeEndToEndTests : IDisposable
{
    private const string Generator = ReferenceGridPageCodeGenerator.GeneratorName;
    private const string Header = "Statement";
    private const int OneBlockRecords = 20;

    private static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(30);

    // Right of the narrowed table, inside the margins, clear of the numbering area.
    private static readonly PageCodeRectangle CodeArea = new(170, 20, 25, 25);

    private readonly MultiBlockStage _stage = new(
        packageTransform: WithPageCode,
        compositionArguments: ["--PageCodes:ReferenceGrid:Enabled=true"]);

    /// <inheritdoc />
    public void Dispose() => _stage.Dispose();

    [Fact]
    public async Task EveryPageOfAMultiBlockAndAOneBlockDocumentCarriesItsOwnCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);
        await _stage.TakeTemplateAsync(composition, cancellationToken);

        // Several blocks: assembled, numbered and coded.
        var multi = await RunAsync(composition, records: null, "page-code-multi.pdf", cancellationToken);
        var partition = await _stage.ReadPartitionAsync(composition, multi.ReportJobId, cancellationToken);
        Assert.True(partition.BlockCount >= 2, "the snapshot partitions into several blocks");
        AssertCodes(multi.Reading, numbered: true);

        // One block: assembled as one part to draw the code only.
        var one = await RunAsync(composition, OneBlockRecords, "page-code-one.pdf", cancellationToken);
        var onePartition = await _stage.ReadPartitionAsync(composition, one.ReportJobId, cancellationToken);
        Assert.Equal(1, onePartition.BlockCount);
        AssertCodes(one.Reading, numbered: false);

        // The code figures the merge worker records as metrics, as its log line reports
        // them for each output: the reference generator, every page coded, no failure.
        var log = await MultiBlockStage.ReadWorkerLogAsync(composition.App, "merge-worker", cancellationToken);
        foreach (var pages in new[] { multi.Reading.Pages.Count, one.Reading.Pages.Count })
        {
            Assert.Contains(log, line =>
                line.Contains("page codes: generator=" + Generator, StringComparison.Ordinal)
                && line.Contains("origin=reference", StringComparison.Ordinal)
                && line.Contains(string.Create(CultureInfo.InvariantCulture, $"pages={pages} "), StringComparison.Ordinal)
                && line.Contains("failures=0", StringComparison.Ordinal));
        }
    }

    private async Task<(string ReportJobId, PdfDocumentReading Reading)> RunAsync(
        MultiBlockStage.Composition composition,
        int? records,
        string fileName,
        CancellationToken cancellationToken)
    {
        var uploadId = await _stage.SeedConfirmingUploadAsync(composition, cancellationToken, records);
        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);
        var ended = await _stage.WaitForEndAsync(composition, reportJobId, cancellationToken, JobDeadline);

        Assert.Equal("SUCCEEDED", ended.Status);
        Assert.Equal("SUCCEEDED", ended.OutputStatus);
        Assert.Null(ended.OutputErrorCode);
        var path = await _stage.DownloadAsync(
            composition,
            ended.ObjectKey!,
            ended.SizeBytes!.Value,
            ended.Sha256!,
            fileName,
            cancellationToken);
        return (reportJobId, PdfWordReader.ReadDocument(path, MultiBlockStage.Margins));
    }

    private void AssertCodes(PdfDocumentReading reading, bool numbered)
    {
        var total = reading.Pages.Count;
        var generator = new ReferenceGridPageCodeGenerator();
        var region = CodeArea.Region;
        foreach (var page in reading.Pages)
        {
            var value = string.Create(
                CultureInfo.InvariantCulture,
                $"{_stage.TemplateId}|{page.Number}/{total}|{Header}");
            var expected = generator.Generate(value, new Dictionary<string, string>(), CodeArea.Size).Content!;

            var form = Assert.Single(page.Graphics!.Forms, candidate =>
                candidate.BoundingBox is { Count: 4 } box
                && Math.Abs(box[2] - region.WidthPt) < 0.05
                && Math.Abs(box[3] - region.HeightPt) < 0.05);
            Assert.Equal(expected, form.Content);
            var drawing = Assert.Single(page.Graphics.Drawings, candidate => candidate.Name == form.Name);
            Assert.Equal(region.LeftPt, drawing.Matrix[4], 1);
            Assert.Equal(page.Page.Height - region.TopPt - region.HeightPt, drawing.Matrix[5], 1);
            Assert.Empty(PageCodeText.Inside(page.Page, region));

            var stamp = string.Join(' ', page.Page.Words.Where(word => MultiBlockStage.Inside(word, MultiBlockStage.Area)).Select(word => word.Text));
            if (numbered)
            {
                Assert.Contains(page.Number.ToString(CultureInfo.InvariantCulture), stamp, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(page.Page.Words, word => word.Text == "PART-COUNTER");
            }
        }
    }

    // The stage's own package, with the table narrowed to leave a column for the code
    // inside the right margin and the per-page code declared on the reference generator.
    private static Dictionary<string, byte[]> WithPageCode(Dictionary<string, byte[]> files)
    {
        var styles = Encoding.UTF8.GetString(files["html/styles.css"]);
        files["html/styles.css"] = Encoding.UTF8.GetBytes(
            styles + "\ntable.items { width: 150mm; }\n.document-header { width: 150mm; }\n");

        var manifest = Encoding.UTF8.GetString(files["template.yaml"]);
        var newline = manifest.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = manifest.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var pdf = lines.FindIndex(line => string.Equals(line.Trim(), "pdf:", StringComparison.Ordinal));
        var indent = new string(' ', lines[pdf].Length - lines[pdf].TrimStart().Length + 2);
        lines.InsertRange(pdf + 1,
        [
            indent + "pageCode:",
            indent + "  generator: " + Generator,
            indent + "  area: { leftMm: 170, topMm: 20, widthMm: 25, heightMm: 25 }",
            indent + "  source:",
            indent + "    value: \"{template.id}|{page.number}/{page.total}|{match}\"",
            indent + "    match: { pattern: \"" + Header + "\", maxLength: 9 }",
        ]);
        files["template.yaml"] = Encoding.UTF8.GetBytes(string.Join(newline, lines));
        return files;
    }
}
