using ReportService.Rendering.Layout;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.Rendering.Tests.Layout;

/// <summary>
/// The layout assets of the accounting print: the vendored Paged.js 0.4.3 is the pinned
/// file, unchanged; the three inline scripts are allowed by their own SHA-256 hash
/// sources and no host source; the accounting policy allows no template script and the
/// inline styles Paged.js inserts at run time; the native policy stays hash-only.
/// </summary>
public sealed class LayoutAssetsTests
{
    [Fact]
    public void TheVendoredPolyfillIsThePinnedFile()
    {
        Assert.Equal(
            "f59f361802416c770d549a647958649af2cf6601999924bc00e4f507dad5269f",
            LayoutAssets.Sha256Of(LayoutAssets.PagedPolyfill));
        Assert.Equal(921_702, System.Text.Encoding.UTF8.GetByteCount(LayoutAssets.PagedPolyfill));
    }

    [Fact]
    public void TheConfigurationKeepsPagedFromStartingBeforeTheSplit()
    {
        Assert.Equal("window.PagedConfig = { auto: false };", LayoutAssets.ConfigurationScript);
    }

    [Fact]
    public void TheLayoutScriptSplitsBeforeLayoutAndAccountsAfterIt()
    {
        Assert.Contains("PagedConfig", LayoutAssets.ConfigurationScript, StringComparison.Ordinal);
        Assert.Contains("new Paged.Previewer().preview()", LayoutAssets.ProductScript, StringComparison.Ordinal);
        Assert.Contains("dataset.layout = \"done\"", LayoutAssets.ProductScript, StringComparison.Ordinal);
        Assert.Contains("(continues)", LayoutAssets.ProductScript, StringComparison.Ordinal);
        Assert.Contains("BigInt", LayoutAssets.ProductScript, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThreeScriptsAreAllowedByTheirOwnHashes()
    {
        var policy = ChromiumPdfPrinter.AccountedPolicy;
        var scriptSrc = SourceOf(policy, "script-src");

        foreach (var hash in LayoutAssets.ScriptHashSources)
        {
            Assert.Contains(hash, scriptSrc, StringComparison.Ordinal);
        }

        Assert.Equal(3, LayoutAssets.ScriptHashSources.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheAccountingPolicyAllowsNoTemplateScript()
    {
        var scriptSrc = SourceOf(ChromiumPdfPrinter.AccountedPolicy, "script-src");

        Assert.DoesNotContain("unsafe-inline", scriptSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", scriptSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", scriptSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAccountingPolicyAllowsInlineStylesForThatPrintOnly()
    {
        var policy = ChromiumPdfPrinter.AccountedPolicy;

        Assert.Contains("'unsafe-inline'", SourceOf(policy, "style-src"), StringComparison.Ordinal);
        Assert.Contains("https://report.invalid", SourceOf(policy, "style-src"), StringComparison.Ordinal);
        Assert.Contains("https://report.invalid", SourceOf(policy, "connect-src"), StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", ChromiumPdfPrinter.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNativePolicyAllowsExactlyThePageRuleAndNoInlineSyntax()
    {
        var rule = PageRule.From(new PdfPageSettings(
            "A4",
            PageOrientation.Portrait,
            210,
            297,
            15,
            12,
            18,
            12));

        var policy = ChromiumPdfPrinter.PolicyFor(rule);

        Assert.Contains(rule.CspHashSource, policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", policy, StringComparison.Ordinal);
        Assert.Contains("!important", rule.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptsAreServedAfterTheMarkupInTheOrderTheSplitNeeds()
    {
        var document = LayoutAssets.AppendTo("<html><body><p>block</p><style>@page{}</style></body></html>");

        var configuration = document.IndexOf("window.PagedConfig", StringComparison.Ordinal);
        var polyfill = document.IndexOf("pagedjs-inserted-styles", StringComparison.Ordinal);
        var product = document.IndexOf("window.__pageAccounting", StringComparison.Ordinal);

        Assert.True(configuration > 0);
        Assert.True(polyfill > configuration);
        Assert.True(product > polyfill);
        Assert.EndsWith("</script>", document, StringComparison.Ordinal);
    }

    private static string SourceOf(string policy, string name)
    {
        foreach (var directive in policy.Split(';'))
        {
            var trimmed = directive.Trim();
            if (trimmed.StartsWith(name + " ", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return string.Empty;
    }
}
