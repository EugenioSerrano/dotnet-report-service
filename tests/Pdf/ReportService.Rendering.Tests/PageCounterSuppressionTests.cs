using ReportService.Rendering;

namespace ReportService.Rendering.Tests;

/// <summary>
/// The multi-block part's counter override: it empties exactly the page-margin
/// boxes whose content carries a page counter, keeps a running header untouched,
/// and adds nothing at all when the document declares no counter. The page rule's
/// content-security hash follows the combined text it serves.
/// </summary>
public sealed class PageCounterSuppressionTests
{
    private const string Document = """
        <html><head><style>
        @page {
          @bottom-center { content: "Page " counter(page) " of " counter(pages); }
          @top-center { content: "Statement"; }
        }
        </style></head><body>rows</body></html>
        """;

    [Fact]
    public void ItEmptiesOnlyTheCounterBoxes()
    {
        var css = PageCounterSuppression.OverrideFor(Document);

        Assert.Contains("@bottom-center { content: none !important; }", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@top-center", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithNoPageCounterGetsNoOverride() =>
        Assert.Equal(string.Empty, PageCounterSuppression.OverrideFor("<html><style>@page { @top-center { content: \"Header\"; } }</style></html>"));

    [Fact]
    public void ACounterWithSpacesIsStillFound()
    {
        var css = PageCounterSuppression.OverrideFor(
            "<style>@page { @bottom-right { content: \"x\" counter( page ); } }</style>");

        Assert.Contains("@bottom-right { content: none !important; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdditionalCssIsCoveredByTheRulesOwnHash()
    {
        var rule = PageRule.From(new ReportService.TemplatePackages.Manifest.PdfPageSettings(
            "A4",
            ReportService.TemplatePackages.Manifest.PageOrientation.Portrait,
            210,
            297,
            12,
            10,
            12,
            10));

        var combined = rule.WithAdditionalCss("@page { @bottom-center { content: none !important; } }");

        Assert.NotEqual(rule.CspHashSource, combined.CspHashSource);
        Assert.StartsWith(rule.Css, combined.Css, StringComparison.Ordinal);
        Assert.EndsWith("@page { @bottom-center { content: none !important; } }", combined.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAdditionalCssLeavesTheRuleAsItWas()
    {
        var rule = PageRule.From(new ReportService.TemplatePackages.Manifest.PdfPageSettings(
            "A4",
            ReportService.TemplatePackages.Manifest.PageOrientation.Portrait,
            210,
            297,
            12,
            10,
            12,
            10));

        Assert.Same(rule, rule.WithAdditionalCss(string.Empty));
    }
}
