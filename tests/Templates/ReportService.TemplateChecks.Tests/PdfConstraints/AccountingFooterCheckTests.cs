using ReportService.TemplateChecks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplateSource.Checks;
using ReportService.Testing.TemplatePackages;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The footer rule of the accounting path: a version that prints page totals reserves the
/// band the values are written into. The rectangle must be present, positive and inside
/// the bottom margin band, the three labels must be declared, the amount field and the
/// scale must be usable, and the band must not overlap a declared global-numbering area.
/// A version without page totals is unaffected.
/// </summary>
public sealed class AccountingFooterCheckTests
{
    [Theory]
    [InlineData("scriban")]
    [InlineData("handlebars")]
    [InlineData("liquid")]
    public async Task AnAccountedPackagePasses(string engine)
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.Accounted(engine));
        var candidate = Packages.ReadCandidate(package.Directory);

        var result = await Check(candidate);

        Assert.True(result.Passed, result.Reason);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData("scriban")]
    [InlineData("handlebars")]
    [InlineData("liquid")]
    public async Task AVersionWithoutPageTotalsPasses(string engine)
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.Control(engine));
        var candidate = Packages.ReadCandidate(package.Directory);

        var result = await Check(candidate);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AVeryVersionThatPrintsTotalsWithoutTheBandIsRefused()
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.PageTotalsEnabled("scriban"));
        var candidate = Packages.ReadCandidate(package.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("footer", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("template.yaml:", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADimensionThatIsNotPositiveIsRefused()
    {
        using var written = WithManifest("        widthMm: 186\n", "        widthMm: 0\n");
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Contains("not positive", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABandThatCrossesTheBottomMarginIsRefused()
    {
        using var written = WithManifest("        topMm: 282\n", "        topMm: 270\n");
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Contains("bottom margin", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABandThatRunsPastThePageWidthIsRefused()
    {
        using var written = WithManifest("        widthMm: 186\n", "        widthMm: 200\n");
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Contains("right edge", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingLabelIsRefused()
    {
        using var written = WithManifest("carriedForward: \"Saldo siguiente\"\n", "carriedForward: \"\"\n");
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Contains("three labels", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingAmountFieldIsRefused()
    {
        using var written = WithManifest("      amountField: amount\n", "      amountField: \"\"\n");
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Contains("amountField", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScaleThatIsNotAWholeNumberOfDigitsIsRefused()
    {
        using var written = WithManifest("      scale: 2\n", "      scale: 2.5\n");
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Contains("scale", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABandOverlappingTheNumberingAreaIsRefused()
    {
        using var written = WithManifest("    verification:", Numbering(282));
        var candidate = Packages.ReadCandidate(written.Directory);
        Assert.NotNull(candidate.Manifest.Outputs?.Pdf?.Numbering);
        Assert.Equal(282m, candidate.Manifest.Outputs!.Pdf!.Numbering!.Area!.TopMm);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Contains("numbering area", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABandBesideTheNumberingAreaIsAdmitted()
    {
        using var written = WithManifest("    verification:", Numbering(240));
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.True(result.Passed, result.Reason);
    }

    /// <summary>The numbering contract a manifest declares at the given top, in millimetres.</summary>
    private static string Numbering(int topMm) =>
        "    numbering:\n"
        + "      area:\n"
        + "        leftMm: 12\n"
        + "        topMm: " + topMm + "\n"
        + "        widthMm: 100\n"
        + "        heightMm: 12\n"
        + "      font:\n"
        + "        family: \"Liberation Sans\"\n"
        + "        file: html/font.ttf\n"
        + "        sizePt: 9\n"
        + "      format: \"Page {X} of {Y}\"\n"
        + "      culture: es-AR\n"
        + "    verification:";

    [Fact]
    public void TheCheckIsNamedAndCarriesItsRuleIdentifier()
    {
        Assert.Equal("AccountingFooterCheck", new AccountingFooterCheck().Name);
        Assert.Equal("pdf-accounting-footer", AccountingFooterCheck.RuleId);
        Assert.Equal(TemplateRefusal.Rules.AccountingFooter, AccountingFooterCheck.RuleId);
    }

    private static TempPackage WithManifest(string from, string to)
    {
        var manifest = PdfConstraintFixtures.AccountedManifest("scriban").Replace(from, to, StringComparison.Ordinal);
        var package = PdfConstraintFixtures.Accounted("scriban");
        package["template.yaml"] = System.Text.Encoding.UTF8.GetBytes(manifest);
        return Packages.WritePackage(package);
    }

    private static Task<TemplateCheckResult> Check(TemplateCandidate candidate) =>
        new AccountingFooterCheck().CheckAsync(candidate, TestContext.Current.CancellationToken);
}
