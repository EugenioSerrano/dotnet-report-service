using ReportService.TemplateChecks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplateSource.Checks;
using ReportService.Testing.TemplatePackages;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The detail rule of the accounting path: a version that prints page totals is admitted
/// only with grid detail — one root element per row, an amount cell marked with a literal
/// role, and, when the version groups its records, a group total cell marked the same way.
/// Table markup, a second root and a missing marker are refused; a role a template
/// expression supplies is recorded, never guessed; a version without page totals is
/// unaffected.
/// </summary>
public sealed class AccountedDetailMarkupCheckTests
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
    public async Task AVersionWithoutPageTotalsIsUnaffected(string engine)
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.Control(engine));
        var candidate = Packages.ReadCandidate(package.Directory);

        var result = await Check(candidate);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task TableMarkupForDetailIsRefused()
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.PageTotalsEnabled("scriban"));
        var candidate = Packages.ReadCandidate(package.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("table markup", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("html/row.html.sbn", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowPartWithTwoRootElementsIsRefused()
    {
        var package = PdfConstraintFixtures.Accounted("scriban");
        package["html/row.html.sbn"] = System.Text.Encoding.UTF8.GetBytes(
            "<div class=\"item\"><div class=\"desc\">{{ item.description | html.escape }}</div><div class=\"amount\" data-role=\"amount\">{{ item.amount | html.escape }}</div></div>"
            + "<div class=\"extra\">x</div>");
        using var written = Packages.WritePackage(package);
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("root elements", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowPartWithoutTheAmountRoleIsRefused()
    {
        var package = PdfConstraintFixtures.Accounted("scriban");
        package["html/row.html.sbn"] = System.Text.Encoding.UTF8.GetBytes(
            "<div class=\"item\"><div class=\"desc\">{{ item.description | html.escape }}</div><div class=\"amount\">{{ item.amount | html.escape }}</div></div>");
        using var written = Packages.WritePackage(package);
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("data-role=\"amount\"", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupedVersionWithoutTheGroupTotalRoleIsRefused()
    {
        var package = PdfConstraintFixtures.Accounted("scriban");
        package["html/group-end.html.sbn"] = System.Text.Encoding.UTF8.GetBytes(
            "<div class=\"group-total\">{{ group.key | html.escape }}<span></span></div></div>");
        using var written = Packages.WritePackage(package);
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("data-role=\"group-total\"", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoleSuppliedByATemplateExpressionIsRecordedNotRefused()
    {
        var package = PdfConstraintFixtures.Accounted("scriban");
        package["html/row.html.sbn"] = System.Text.Encoding.UTF8.GetBytes(
            "<div class=\"item\"><div class=\"desc\">{{ item.description | html.escape }}</div><div class=\"amount\" data-role=\"{{ item.group }}\">{{ item.amount | html.escape }}</div></div>");
        using var written = Packages.WritePackage(package);
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AGroupEndPartWithTableMarkupIsRefused()
    {
        var package = PdfConstraintFixtures.Accounted("scriban");
        package["html/group-end.html.sbn"] = System.Text.Encoding.UTF8.GetBytes(
            "<tr class=\"group-total\"><td>{{ group.key | html.escape }}</td><td data-role=\"group-total\"></td></tr></div>");
        using var written = Packages.WritePackage(package);
        var candidate = Packages.ReadCandidate(written.Directory);

        var result = await Check(candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("table markup", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheckIsNamedAndCarriesItsRuleIdentifier()
    {
        using var session = new TemplateCheckSession(TimeProvider.System);

        Assert.Equal("AccountedDetailMarkupCheck", new AccountedDetailMarkupCheck(session).Name);
        Assert.Equal("pdf-accounted-detail", AccountedDetailMarkupCheck.RuleId);
        Assert.Equal(TemplateRefusal.Rules.AccountedDetail, AccountedDetailMarkupCheck.RuleId);
    }

    private static Task<TemplateCheckResult> Check(TemplateCandidate candidate) =>
        new AccountedDetailMarkupCheck(new TemplateCheckSession(TimeProvider.System))
            .CheckAsync(candidate, TestContext.Current.CancellationToken);
}
