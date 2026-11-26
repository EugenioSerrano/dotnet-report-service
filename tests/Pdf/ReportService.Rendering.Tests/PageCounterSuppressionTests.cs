using System.Text;
using ReportService.Rendering;
using ReportService.Templates;

namespace ReportService.Rendering.Tests;

/// <summary>
/// The multi-block part's counter override: it empties exactly the page-margin
/// boxes whose content carries a page counter, keeps a running header untouched,
/// and adds nothing at all when the document declares no counter. The counters may
/// be declared in the document's own style element or in a stylesheet the document
/// links - the shipped templates do the latter - and the override is built from the
/// same rules the printer serves. The page rule's content-security hash follows the
/// combined text it serves.
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

    private const string LinkedDocument =
        """<html><head><link rel="stylesheet" href="html/styles.css"></head><body>rows</body></html>""";

    private const string LinkedCss = """
        @page {
          @bottom-center { content: "Page " counter(page) " of " counter(pages); }
          @top-center { content: "Statement"; }
        }
        """;

    [Fact]
    public void ItEmptiesOnlyTheCounterBoxes()
    {
        var css = PageCounterSuppression.OverrideFor(Document, Package.None);

        Assert.Contains("@bottom-center { content: none !important; }", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@top-center", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithNoPageCounterGetsNoOverride() =>
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor(
                "<html><style>@page { @top-center { content: \"Header\"; } }</style></html>",
                Package.None));

    [Fact]
    public void ACounterWithSpacesIsStillFound()
    {
        var css = PageCounterSuppression.OverrideFor(
            "<style>@page { @bottom-right { content: \"x\" counter( page ); } }</style>",
            Package.None);

        Assert.Contains("@bottom-right { content: none !important; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ACounterDeclaredInALinkedPackageStylesheetIsFound()
    {
        // The shipped templates declare their page rule in html/styles.css linked from
        // document-start: scanning the composed HTML alone found nothing and the part
        // still printed its own counter (run 261126-1517).
        var css = PageCounterSuppression.OverrideFor(
            LinkedDocument,
            new Package(new Dictionary<string, string>(StringComparer.Ordinal) { ["html/styles.css"] = LinkedCss }));

        Assert.Contains("@bottom-center { content: none !important; }", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@top-center", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkedStylesheetWithAnAbsolutePackagePathIsFound()
    {
        var css = PageCounterSuppression.OverrideFor(
            """<link rel="stylesheet" href="/html/styles.css">""",
            new Package(new Dictionary<string, string>(StringComparer.Ordinal) { ["html/styles.css"] = LinkedCss }));

        Assert.Contains("@bottom-center { content: none !important; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkedStylesheetOutsideThePackageContributesNothing()
    {
        var files = new Package(new Dictionary<string, string>(StringComparer.Ordinal) { ["html/styles.css"] = LinkedCss });

        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor(
                """<link rel="stylesheet" href="https://elsewhere.invalid/html/styles.css">""",
                files));
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor(
                """<link rel="stylesheet" href="html/missing.css">""",
                files));
    }

    [Fact]
    public void AnEmptyHrefIsIgnored() =>
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor("""<link rel="stylesheet" href="">""", LinkedFiles));

    [Fact]
    public void ALinkThatDoesNotNameAStylesheetIsIgnored()
    {
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor("""<link rel="preload" href="html/styles.css">""", LinkedFiles));
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor("""<link href="html/styles.css">""", LinkedFiles));
    }

    [Fact]
    public void ALinkLikeWordInTextIsNotALink() =>
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor("""<p><linkage rel="stylesheet" href="html/styles.css"></p>""", LinkedFiles));

    [Fact]
    public void ALinkWithoutAClosingBracketIsIgnored() =>
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor("""<link rel="stylesheet" href="html/styles.css""", LinkedFiles));

    [Fact]
    public void AnAttributeWithSpacesAroundTheEqualsIsRead()
    {
        var css = PageCounterSuppression.OverrideFor(
            """<link rel = "stylesheet" href = "html/styles.css">""",
            LinkedFiles);

        Assert.Contains("@bottom-center { content: none !important; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnquotedAttributeIsRead()
    {
        var css = PageCounterSuppression.OverrideFor(
            """<link rel=stylesheet href=html/styles.css>""",
            LinkedFiles);

        Assert.Contains("@bottom-center { content: none !important; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttributeWhoseNameEndsAnotherNameIsNotTheAttribute()
    {
        // The data-href must not answer for href.
        var css = PageCounterSuppression.OverrideFor(
            """<link rel="stylesheet" data-href="other.css" href="html/styles.css">""",
            LinkedFiles);

        Assert.Contains("@bottom-center { content: none !important; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkWhoseAttributeHasNoValueIsIgnored()
    {
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor("""<link rel="stylesheet" href=>""", LinkedFiles));
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor("""<link rel href="html/styles.css">""", LinkedFiles));
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor("""<link rel="stylesheet href=html/styles.css>""", LinkedFiles));
    }

    [Fact]
    public void AnUnclosedStyleElementIsIgnored()
    {
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor(
                """<style>@page { @bottom-center { content: "x" counter(page); } }""",
                Package.None));
        Assert.Equal(string.Empty, PageCounterSuppression.OverrideFor("<style", Package.None));
    }

    [Fact]
    public void AnUnbalancedPageRuleIsIgnored()
    {
        Assert.Equal(string.Empty, PageCounterSuppression.OverrideFor("<style>@page </style>", Package.None));
        Assert.Equal(
            string.Empty,
            PageCounterSuppression.OverrideFor(
                """<style>@page { @bottom-center { content: "x" </style>""",
                Package.None));
    }

    private static Package LinkedFiles =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["html/styles.css"] = LinkedCss });


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

    /// <summary>A package held in memory, so the linked rules are read without a folder.</summary>
    private sealed class Package(IReadOnlyDictionary<string, string> files) : ITemplatePackageFiles
    {
        public static Package None { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

        public bool Exists(string path) => files.ContainsKey(path);

        public Stream OpenRead(string path) =>
            files.TryGetValue(path, out var text)
                ? new MemoryStream(Encoding.UTF8.GetBytes(text))
                : throw new TemplatePackageException($"The package holds no file named '{path}'.");

        public string ReadAllText(string path) =>
            files.TryGetValue(path, out var text)
                ? text
                : throw new TemplatePackageException($"The package holds no file named '{path}'.");
    }
}
