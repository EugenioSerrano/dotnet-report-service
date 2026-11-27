using ReportService.TemplatePackages.Manifest;
using ReportService.Testing.Fixtures;
using ReportService.Testing.TemplatePackages;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The page-accounting fixture package and its committed declaration: the fixture passes
/// every static check the take registers — the two accounting admission rules included —
/// so the end-to-end case that takes it has a package the take accepts, and the
/// declaration in <c>OracleExpectations/page-accounting.json</c> equals the fixture's own
/// manifest, so the oracle cannot drift from the package it judges.
/// </summary>
public sealed class PageAccountingFixtureTests
{
    private const string Consumer = "billing-system";

    [Fact]
    public async Task TheFixturePassesEveryStaticCheckTheTakeRegisters()
    {
        using var package = Packages.WritePackage(PageAccountingFixtures.Files(Consumer));
        var candidate = Packages.ReadCandidate(package.Directory);

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.True(result.Passed, result.Code + ": " + result.Reason);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void TheDeclaredBandAndLabelsAreTheFixturesOwn()
    {
        using var package = Packages.WritePackage(PageAccountingFixtures.Files(Consumer));
        var candidate = Packages.ReadCandidate(package.Directory);
        var totals = candidate.Manifest.Outputs?.Pdf?.PageTotals
            ?? throw new InvalidOperationException("the fixture declares no outputs.pdf.pageTotals.");

        Assert.True(totals.Enabled);
        Assert.Equal(PageAccountingFixtures.AmountField, totals.AmountField);
        Assert.Equal(PageAccountingFixtures.Scale, (int)(totals.Scale ?? 0));
        Assert.Equal(PageAccountingFixtures.BroughtForwardLabel, totals.Footer?.Labels?.BroughtForward);
        Assert.Equal(PageAccountingFixtures.PageSubtotalLabel, totals.Footer?.Labels?.PageSubtotal);
        Assert.Equal(PageAccountingFixtures.CarriedForwardLabel, totals.Footer?.Labels?.CarriedForward);
        Assert.Equal(PageAccountingFixtures.BandLeftMm, totals.Footer?.LeftMm);
        Assert.Equal(PageAccountingFixtures.BandTopMm, totals.Footer?.TopMm);
        Assert.Equal(PageAccountingFixtures.BandWidthMm, totals.Footer?.WidthMm);
        Assert.Equal(PageAccountingFixtures.BandHeightMm, totals.Footer?.HeightMm);
        Assert.Equal(PageAccountingFixtures.GroupKey, candidate.Manifest.Partitioning?.GroupKey);
    }

    [Fact]
    public void TheDeclaredNumberingContractIsTheFixturesOwn()
    {
        using var package = Packages.WritePackage(PageAccountingFixtures.Files(Consumer));
        var candidate = Packages.ReadCandidate(package.Directory);
        var numbering = candidate.Manifest.Outputs?.Pdf?.Numbering
            ?? throw new InvalidOperationException("the fixture declares no outputs.pdf.numbering.");

        Assert.Equal(PageAccountingFixtures.NumberingFormat, numbering.Format);
        Assert.Equal(PageAccountingFixtures.Culture, numbering.Culture);
        Assert.Equal(PageAccountingFixtures.NumberingLeftMm, numbering.Area?.LeftMm);
        Assert.Equal(PageAccountingFixtures.NumberingTopMm, numbering.Area?.TopMm);
        Assert.Equal(PageAccountingFixtures.NumberingWidthMm, numbering.Area?.WidthMm);
        Assert.Equal(PageAccountingFixtures.NumberingHeightMm, numbering.Area?.HeightMm);
        Assert.Equal(PageAccountingFixtures.FontPath, numbering.Font?.File);
    }

    [Fact]
    public void TheCommittedDeclarationMatchesTheFixture()
    {
        var declaration = PageAccountingDeclaration.Load();

        Assert.Equal(PageAccountingFixtures.AmountField, declaration.AmountField);
        Assert.Equal(PageAccountingFixtures.GroupKey, declaration.GroupKey);
        Assert.Equal(PageAccountingFixtures.Scale, declaration.Scale);
        Assert.Equal(PageAccountingFixtures.Culture, declaration.Culture);
        Assert.Equal(PageAccountingFixtures.BroughtForwardLabel, declaration.BroughtForwardLabel);
        Assert.Equal(PageAccountingFixtures.PageSubtotalLabel, declaration.PageSubtotalLabel);
        Assert.Equal(PageAccountingFixtures.CarriedForwardLabel, declaration.CarriedForwardLabel);
        Assert.Equal(PageAccountingFixtures.NumberingFormat, declaration.NumberingFormat);
        Assert.Equal(PageAccountingFixtures.Culture, declaration.NumberingCulture);
        Assert.Equal((double)PageAccountingFixtures.NumberingLeftMm, declaration.NumberingLeftMm);
        Assert.Equal((double)PageAccountingFixtures.NumberingTopMm, declaration.NumberingTopMm);
        Assert.Equal((double)PageAccountingFixtures.NumberingWidthMm, declaration.NumberingWidthMm);
        Assert.Equal((double)PageAccountingFixtures.NumberingHeightMm, declaration.NumberingHeightMm);
    }

    [Fact]
    public void TheNonNumericAmountFieldVariantIsAcceptedByTheAdmissionRulesAndIsNotANumber()
    {
        // The known-bad control of the end-to-end case: a package the take admits whose amount
        // field carries text, so the worker's marking refuses it at render and nothing is published.
        using var package = Packages.WritePackage(
            PageAccountingFixtures.Files(Consumer, PageAccountingFixtures.TemplateId, amountField: "description"));
        var candidate = Packages.ReadCandidate(package.Directory);
        var totals = candidate.Manifest.Outputs?.Pdf?.PageTotals;

        Assert.Equal("description", totals?.AmountField);
        Assert.True(totals?.Enabled);
    }
}
