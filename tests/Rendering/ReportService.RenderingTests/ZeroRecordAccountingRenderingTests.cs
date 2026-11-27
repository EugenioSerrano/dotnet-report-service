using System.Globalization;
using ReportService.Pdf.Verification;
using ReportService.Testing.Fixtures;
using ReportService.Templates;
using ReportService.Testing.TemplatePackages;

namespace ReportService.RenderingTests;

/// <summary>
/// The zero-record accounting instance of ADR-021 §3.11.4, printed by the rendering host
/// over the fixture library's <c>K0</c> (the empty snapshot) and judged by the independent
/// source oracle: block 0 lays out with no rows, every page's subtotal, brought forward and
/// carried forward is 0, the chain closes on the block total 0, and the band prints its
/// three labelled zeros on every page. The template's own header is what the page prints;
/// the service injects nothing.
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class ZeroRecordAccountingRenderingTests(RenderingSuite suite)
{
    private static readonly PageMargins Margins = PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12));

    [Fact]
    public async Task AZeroRecordBlockPrintsZerosAndItsChainClosesOnBlockTotalZero()
    {
        var render = await suite.RenderAsync(Fixture, PageAccountingFixtures.TemplateId);

        Assert.True(render.VerificationPassed, render.VerificationReason);
        Assert.NotNull(render.Pdf);
        var reading = PdfWordReader.Read(render.Pdf!, Margins);
        Assert.True(reading.Pages.Count >= 1, "the empty report still prints a page (C-5)");

        var declaration = PageAccountingDeclaration.Load();
        var records = PageAccountingExpectations.ReadRecords(
            Path.Combine(suite.FixturesDirectory, Fixture + ".json"),
            declaration.AmountField,
            declaration.GroupKey);
        Assert.Empty(records);

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

        var problems = AccountedDocumentCheck.Verify(reading, declaration, expected, attribution, expectNumbering: false);
        Assert.True(problems.Count == 0, string.Join("; ", problems));

        // Every page's three values are zero, and no row's amount prints anywhere.
        foreach (var page in reading.Pages)
        {
            var lines = PageAccountingExpectations.ReadBand(page, declaration);
            Assert.True(PageAccountingExpectations.TryParseBand(lines.BroughtForward, declaration.Scale, declaration.Culture, out var brought));
            Assert.True(PageAccountingExpectations.TryParseBand(lines.PageSubtotal, declaration.Scale, declaration.Culture, out var subtotal));
            Assert.True(PageAccountingExpectations.TryParseBand(lines.CarriedForward, declaration.Scale, declaration.Culture, out var carried));
            Assert.Equal(0m, brought);
            Assert.Equal(0m, subtotal);
            Assert.Equal(0m, carried);
        }

        Assert.All(
            reading.Pages.SelectMany(page => page.Words).Where(word => word.Band == ReadingBand.Content),
            word => Assert.False(
                decimal.TryParse(word.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
                $"the empty block printed the value \"{word.Text}\" on the page"));

        Assert.Equal(0m, expected.GrandTotal);
        TestContext.Current.TestOutputHelper?.WriteLine($"zero record: pages={reading.Pages.Count}");
    }

    /// <summary>The fixture library's empty snapshot: the header, zero records, record count 0.</summary>
    private const string Fixture = "K0";
}
