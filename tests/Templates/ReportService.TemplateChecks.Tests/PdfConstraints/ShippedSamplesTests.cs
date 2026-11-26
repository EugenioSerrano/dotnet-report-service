using ReportService.TemplateChecks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.Testing;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The product ships three sample templates, and a sample the product prints must be
/// one its own take accepts: each pilot's own files are read as the tree carries them
/// and every PDF rule this build registers admits them. The declarations the rules
/// admit are pinned beside the verdict, so a pilot that names only a generic family,
/// turns the printed totals back on or loses the declaration fails here by name
/// instead of drifting silently.
/// </summary>
public sealed class ShippedSamplesTests
{
    private const string NamedFamily = "font-family: \"Liberation Sans\", sans-serif;";

    [Theory]
    [InlineData("pilot-simple")]
    [InlineData("pilot-grouped")]
    [InlineData("pilot-volume")]
    public async Task AShippedSampleIsAdmittedByEveryPdfRuleTheBuildRegisters(string pilot)
    {
        var folder = Path.Combine(RepositoryPaths.Root, "samples", "templates", pilot);
        var candidate = Packages.ReadCandidate(folder);
        using var session = new TemplateCheckSession(TimeProvider.System);

        var pageAccounting = await new AccountedDetailMarkupCheck(session).CheckAsync(candidate, TestContext.Current.CancellationToken);
        var accountingFooter = await new AccountingFooterCheck().CheckAsync(candidate, TestContext.Current.CancellationToken);
        var fontFamily = await new NamedFontFamilyCheck(session).CheckAsync(candidate, TestContext.Current.CancellationToken);
        var gridCell = await new BottomAlignedGridCellCheck(session).CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(pageAccounting.Passed, pilot + ": " + pageAccounting.Reason);
        Assert.Null(pageAccounting.Reason);
        Assert.True(accountingFooter.Passed, pilot + ": " + accountingFooter.Reason);
        Assert.Null(accountingFooter.Reason);
        Assert.True(fontFamily.Passed, pilot + ": " + fontFamily.Reason);
        Assert.Null(fontFamily.Reason);
        Assert.True(gridCell.Passed, pilot + ": " + gridCell.Reason);
        Assert.Null(gridCell.Reason);

        var outputs = candidate.Manifest.Outputs
            ?? throw new InvalidOperationException(pilot + " declares no output.");
        var totals = outputs.Pdf?.PageTotals
            ?? throw new InvalidOperationException(pilot + " declares no printed totals member.");
        Assert.False(totals.Enabled, pilot + " must declare the printed totals off.");

        var styles = outputs.Html?.Styles
            ?? throw new InvalidOperationException(pilot + " declares no stylesheet.");
        Assert.NotEmpty(styles);
        foreach (var stylesheet in styles)
        {
            var text = File.ReadAllText(Path.Combine(folder, stylesheet.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains(NamedFamily, text, StringComparison.Ordinal);
        }
    }
}
