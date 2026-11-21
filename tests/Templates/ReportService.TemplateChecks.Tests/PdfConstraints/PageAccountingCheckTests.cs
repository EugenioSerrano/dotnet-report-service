using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The page-subtotal declaration: a PDF version that turns the printed totals on
/// asks for something this build does not print, and it is refused before
/// anything is taken. A version that turns them off, leaves them out, or declares
/// no PDF at all is admitted.
/// </summary>
public sealed class PageAccountingCheckTests
{
    [Fact]
    public async Task APdfVersionThatDeclaresPageTotalsIsRefused()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(WithPageTotals(Packages.ManifestJson(), "true"));

        var result = await new PageAccountingCheck().CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.UnsupportedCapability, result.Code);
        Assert.NotNull(result.Reason);
        Assert.StartsWith("template.yaml:", result.Reason, StringComparison.Ordinal);
        Assert.Contains("page subtotal", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(result.Reason) <= TemplateRefusal.MaximumBytes,
            "the reason must fit the pipeline's limit");
    }

    [Fact]
    public async Task TheMessageEchoesNoDeclaredValue()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(WithPageTotals(Packages.ManifestJson(), "true"));

        var result = await new PageAccountingCheck().CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Reason);
        Assert.DoesNotContain("true", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("false", result.Reason, StringComparison.Ordinal);
        Assert.Contains("outputs.pdf.pageTotals", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageTotalsDeclaredOffAreAdmitted()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(WithPageTotals(Packages.ManifestJson(), "false"));

        var result = await new PageAccountingCheck().CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnAbsentPageTotalsMemberIsAdmitted()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson());

        var result = await new PageAccountingCheck().CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AVersionWithoutPdfOutputIsAdmitted()
    {
        using var package = Packages.Package();
        var candidate = package.Candidate(Packages.ManifestJson(withPdf: false));

        var result = await new PageAccountingCheck().CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public void TheCheckIsNamedAndCarriesItsRuleIdentifier()
    {
        Assert.Equal("PageAccountingCheck", new PageAccountingCheck().Name);
        Assert.Equal("pdf-page-accounting", TemplateRefusal.Rules.PageAccounting);
        Assert.Equal(TemplateRefusal.Rules.PageAccounting, PageAccountingCheck.RuleId);
        Assert.Equal("page-accounting", PageAccountingCheck.ReasonToken);
    }

    [Fact]
    public void TheCheckRunsAfterEveryDeliveredStaticCheckAndBeforeTheFontRule()
    {
        var order = TemplateChecksRegistration.Order;

        Assert.Equal("EngineVersionCheck", order[^6]);
        Assert.Equal("PageAccountingCheck", order[^5]);
        Assert.Equal("NamedFontFamilyCheck", order[^4]);
        Assert.Equal("BottomAlignedGridCellCheck", order[^3]);
        Assert.Equal("sample-render", order[^2]);
        Assert.Equal("at-bound-sample", order[^1]);

        using var provider = new ServiceCollection()
            .AddSingleton<ReportService.TemplateChecks.Sample.ISampleRenderer>(new ReportService.Testing.TemplateChecks.InMemorySampleRenderer().AlwaysRendered())
            .AddTemplateVersionChecks()
            .BuildServiceProvider();
        var names = provider.GetServices<ITemplateVersionCheck>().Select(check => check.Name).ToList();
        Assert.Equal("PageAccountingCheck", names[^5]);
        Assert.Equal("NamedFontFamilyCheck", names[^4]);
        Assert.Equal("BottomAlignedGridCellCheck", names[^3]);
        Assert.Equal("sample-render", names[^2]);
        Assert.Equal("at-bound-sample", names[^1]);
    }

    [Fact]
    public void TheCheckIsAppendedWithoutDisturbingTheDeliveredOrder()
    {
        var order = TemplateChecksRegistration.Order;

        Assert.Equal(
            [
                "ManifestAndPathsCheck",
                "SchemaCheck",
                "SampleCheck",
                "AnchorCheck",
                "SyntaxCheck",
                "IncludeAndAssetCheck",
                "TemplateLintCheck",
                "EngineVersionCheck",
                "PageAccountingCheck",
                "NamedFontFamilyCheck",
                "BottomAlignedGridCellCheck",
                "sample-render",
                "at-bound-sample",
            ],
            order);
    }

    private static string WithPageTotals(string manifestJson, string enabled)
    {
        var manifest = JsonNode.Parse(manifestJson)!.AsObject();
        manifest["outputs"]!["pdf"]!["pageTotals"] = new JsonObject { ["enabled"] = enabled == "true" };
        return manifest.ToJsonString();
    }
}
