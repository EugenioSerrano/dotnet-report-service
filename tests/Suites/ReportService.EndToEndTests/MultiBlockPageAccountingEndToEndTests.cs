using System.Globalization;
using ReportService.Pdf.Verification;
using ReportService.Testing;
using ReportService.Testing.Fixtures;

namespace ReportService.EndToEndTests;

/// <summary>
/// The page-accounting document over the whole local composition: a grouped snapshot of
/// 3 000 records — one group larger than a block under the governed limits — is committed
/// as a multi-block partition while its upload is still confirming, the real Chromium
/// worker renders every block on the accounting path, the real merge worker assembles the
/// parts in manifest order and stamps the global numbering, and the published PDF is read
/// back and compared with the source records by the independent oracle: every page's
/// subtotal is the sum of the rows that ended on it, the brought/carried-forward chain
/// runs across the block boundaries, every group's total is the sum of its own rows even
/// where the group spans a block, and the assembled document's last carried-forward is the
/// grand total of every record. The expected values come from the dataset and the page
/// each row ended on, never from the product's own computation.
/// </summary>
public sealed class MultiBlockPageAccountingEndToEndTests : IDisposable
{
    private readonly MultiBlockStage _stage = new(accounted: true);

    /// <inheritdoc />
    public void Dispose() => _stage.Dispose();

    [Fact]
    public async Task EveryPageGroupAndTheGrandTotalAreExactOnTheAssembledDistribution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);
        await _stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await _stage.SeedConfirmingUploadAsync(composition, cancellationToken);
        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken);
        await _stage.CommitPartitionAsync(composition, reportJobId, uploadId, cancellationToken);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var ended = await _stage.WaitForEndAsync(composition, reportJobId, cancellationToken);

        Assert.True(
            string.Equals(ended.Status, "SUCCEEDED", StringComparison.Ordinal),
            $"the job ended {ended.Status} ({ended.ErrorCode ?? ended.OutputErrorCode ?? "no error code"})");
        Assert.NotNull(ended.ArtifactId);
        Assert.NotNull(ended.OutputId);
        Assert.NotNull(ended.ObjectKey);
        Assert.NotNull(ended.Sha256);
        Assert.NotNull(ended.SizeBytes);
        Assert.StartsWith("outputs/", ended.ObjectKey!, StringComparison.Ordinal);

        var path = await _stage.DownloadAsync(
            composition,
            ended.ObjectKey!,
            ended.SizeBytes!.Value,
            ended.Sha256!,
            "assembled-accounted.pdf",
            cancellationToken);
        var reading = PdfWordReader.Read(path, MultiBlockStage.Margins);
        var declaration = _stage.AccountingDeclaration;
        var records = _stage.AccountingRecords;
        Assert.True(records.Count >= 5_000, "the snapshot reaches the pilot dataset's group larger than a block");

        // The page each row started on and the page it ended on, read from the document itself;
        // then the values from the source records and that attribution.
        var provisional = PageAccountingExpectations.Compute(
            records,
            new Dictionary<long, int>(),
            reading.Pages.Count,
            0m,
            declaration.Scale,
            declaration.Culture);
        var attribution = PageAccountingExpectations.Attribute(reading, provisional);
        var expected = PageAccountingExpectations.Compute(
            records,
            attribution.EndPages,
            reading.Pages.Count,
            0m,
            declaration.Scale,
            declaration.Culture);

        Assert.Equal(records.Count, attribution.EndPages.Count);
        var problems = AccountedDocumentCheck.Verify(reading, declaration, expected, attribution, expectNumbering: true);
        Assert.True(problems.Count == 0, string.Join("; ", problems));

        // The assembled document is more than one block, and its parts are the delivered ones.
        var parts = await _stage.ReadPartsAsync(composition, ended.OutputId!, cancellationToken);
        Assert.True(parts.Count >= 2, "the committed partition holds more than one block");
        Assert.Equal(parts.Sum(part => part.PageCount), reading.Pages.Count);

        // The grand total is the source's own sum, and the last page closes on it.
        Assert.Equal(records.Sum(record => record.Amount), expected.GrandTotal);
        Assert.Equal(expected.GrandTotal, expected.Pages[^1].CarriedForward);

        // The stamp is one continuous global number, never restarted per block.
        Assert.Equal(
            "Pagina 1 de " + reading.Pages.Count.ToString(CultureInfo.InvariantCulture),
            string.Join(' ', PageAccountingExpectations.NumberingWords(reading.Pages[0], declaration)));

        // The evidence stays with the run.
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var evidence = RunEnvironment.WriteEvidence("multi-block-accounted.pdf", bytes);
        RunEnvironment.WriteEvidence(
            "multi-block-accounted.evidence.txt",
            System.Text.Encoding.UTF8.GetBytes(
                $"assembled-accounted.pdf sha256={RunEnvironment.Sha256Of(bytes)} bytes={bytes.LongLength} "
                + $"blocks={parts.Count} pages={reading.Pages.Count} records={records.Count} "
                + $"grandTotal={expected.GrandTotal.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"accounted: {evidence} pages={reading.Pages.Count} groups={expected.Groups.Count}");
    }
}
