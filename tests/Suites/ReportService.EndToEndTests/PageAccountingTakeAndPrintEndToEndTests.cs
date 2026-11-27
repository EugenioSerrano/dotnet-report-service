using ReportService.Pdf.Verification;
using ReportService.Testing;
using ReportService.Testing.Fixtures;
using ReportService.Testing.TemplateChecks;

namespace ReportService.EndToEndTests;

/// <summary>
/// The retired refusals and the printed path over the whole local composition: the
/// page-accounting fixture is published and taken through the real take - the thirteen
/// static checks in their fixed order, the two accounting admission rules included - and a
/// single-block job over it runs through the live path (no staged partition: the data step
/// partitions the 120-record snapshot into one block, within the governed block limits),
/// prints on the accounting path with the real Chromium worker
/// and publishes its PDF. The published document is read back: every page carries its
/// three labelled accounting values, the page subtotals add up to the source's total, and
/// the reserved numbering area is declared and free of accounting words. The global stamp
/// is the assembly's — a single-block PDF is published as Chromium printed it (ADR-007
/// §3.1 item 1) — so this case proves the band and the reserved area are disjoint, not the
/// stamp, which the multi-block case owns.
/// </summary>
public sealed class PageAccountingTakeAndPrintEndToEndTests : IDisposable
{
    private const int Records = 120;

    private readonly MultiBlockStage _stage = new(accounted: true, templateId: "page-accounting-print");

    /// <inheritdoc />
    public void Dispose() => _stage.Dispose();

    [Fact]
    public async Task TheTakeAdmitsTheAccountedPackageAndThePublishedPrintCarriesItsBand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);

        // The take runs the full static list in its fixed order: the retired refusals are
        // proved by a package the checks accept, not by a take that skips them.
        using var checks = new PageAccountingTakeChecks(TimeProvider.System, new InMemorySampleRenderer().AlwaysRendered());
        await _stage.TakeTemplateAsync(composition, cancellationToken, checks.Checks);
        var uploadId = await _stage.SeedConfirmingUploadAsync(composition, cancellationToken, recordsInSnapshot: Records);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken);
        var ended = await _stage.WaitForEndAsync(composition, reportJobId, cancellationToken);

        Assert.True(
            string.Equals(ended.Status, "SUCCEEDED", StringComparison.Ordinal),
            $"the job ended {ended.Status} ({ended.ErrorCode ?? ended.OutputErrorCode ?? "no error code"})");
        Assert.NotNull(ended.ArtifactId);
        Assert.NotNull(ended.ObjectKey);
        Assert.NotNull(ended.Sha256);
        Assert.NotNull(ended.SizeBytes);
        Assert.StartsWith("outputs/", ended.ObjectKey!, StringComparison.Ordinal);

        var path = await _stage.DownloadAsync(
            composition,
            ended.ObjectKey!,
            ended.SizeBytes!.Value,
            ended.Sha256!,
            "single-block-accounted.pdf",
            cancellationToken);
        var reading = PdfWordReader.Read(path, MultiBlockStage.Margins);
        var declaration = _stage.AccountingDeclaration;
        var records = _stage.AccountingRecords;
        Assert.Equal(Records, records.Count);

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

        var problems = AccountedDocumentCheck.Verify(reading, declaration, expected, attribution, expectNumbering: false);
        Assert.True(problems.Count == 0, string.Join("; ", problems));

        // The single block closes on the source's total, and the band is present on every page.
        Assert.Equal(records.Sum(record => record.Amount), expected.GrandTotal);
        Assert.Equal(expected.GrandTotal, expected.Pages[^1].CarriedForward);
        Assert.All(reading.Pages, page =>
        {
            var lines = PageAccountingExpectations.ReadBand(page, declaration);
            Assert.NotNull(lines.BroughtForward);
            Assert.NotNull(lines.PageSubtotal);
            Assert.NotNull(lines.CarriedForward);
        });

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        RunEnvironment.WriteEvidence("single-block-accounted.pdf", bytes);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"accounted single block: pages={reading.Pages.Count} records={records.Count} total={expected.GrandTotal}");
    }
}
