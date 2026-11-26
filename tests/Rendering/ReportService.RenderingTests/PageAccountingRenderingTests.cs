using ReportService.Pdf.Verification;
using ReportService.Testing;
using ReportService.Testing.Fixtures;
using ReportService.Templates;
using ReportService.Testing.TemplatePackages;

namespace ReportService.RenderingTests;

/// <summary>
/// The accounting path printed by the rendering host and judged by the independent source
/// oracle: the fixture library's group-edge snapshot (a multi-page block with a split row
/// and several groups) over the page-accounting package. Every page's subtotal, brought and
/// carried forward, every group's total and the chain closing on the grand total are
/// recomputed from the snapshot's records and the page each row ended on, never from the
/// product's verification; the continuation mark on the split row is proved on its earlier
/// page; and two known-bad controls injected through the host's seam between composition
/// and verification — a printed amount altered after the reference is extracted, and the
/// band's element suppressed — must fail the block with no document produced.
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class PageAccountingRenderingTests(RenderingSuite suite)
{
    private static readonly PageMargins Margins = PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12));

    [Fact]
    public async Task TheAccountedDocumentPrintsItsSubtotalsGroupsAndChain()
    {
        var render = await suite.RenderAsync(PageAccountingRenderingTests.Fixture, PageAccountingFixtures.TemplateId);

        Assert.True(render.VerificationPassed, render.VerificationReason);
        Assert.NotNull(render.Pdf);
        var reading = PdfWordReader.Read(render.Pdf!, Margins);
        Assert.True(reading.Pages.Count > 1, "the fixture block prints more than one page");

        var declaration = PageAccountingDeclaration.Load();
        var records = PageAccountingExpectations.ReadRecords(
            Path.Combine(suite.FixturesDirectory, Fixture + ".json"),
            declaration.AmountField,
            declaration.GroupKey);
        Assert.Equal(250, records.Count);

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

        // The block closes on the source's own total, and at least one row really split.
        Assert.Equal(records.Sum(record => record.Amount), expected.GrandTotal);
        Assert.Equal(expected.GrandTotal, expected.Pages[^1].CarriedForward);
        Assert.Contains(records, record => attribution.StartPages[record.Ordinal] < attribution.EndPages[record.Ordinal]);
        Assert.True(expected.Groups.Count >= 2, "the fixture begins more than one group");

        var evidence = RenderingSuite.CopyToEvidence(render, "page-accounting-ge30.pdf");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"accounted: {evidence} pages={reading.Pages.Count} groups={expected.Groups.Count}");
    }

    [Fact]
    public async Task ATamperedPrintedAmountIsRefusedAndNothingIsProduced()
    {
        var render = await suite.RenderAsync(
            PageAccountingRenderingTests.Fixture,
            PageAccountingFixtures.TemplateId,
            mutation: "AC-AMOUNT");

        Assert.False(render.VerificationPassed);
        Assert.NotNull(render.VerificationReason);
        Assert.Null(render.Pdf);
    }

    [Fact]
    public async Task ASuppressedBandIsRefusedWithTheFooterReason()
    {
        var render = await suite.RenderAsync(
            PageAccountingRenderingTests.Fixture,
            PageAccountingFixtures.TemplateId,
            mutation: "AC-BAND");

        Assert.False(render.VerificationPassed);
        Assert.Equal(VerificationReasons.AccountingFooterMissing, render.VerificationReason);
        Assert.Null(render.Pdf);
    }

    /// <summary>The fixture library's group-edge snapshot: 250 records, several groups and a 30-line row.</summary>
    private const string Fixture = "GE30";
}
