using System.Text;

namespace ReportService.Templates.Tests;

/// <summary>
/// The HTML asset inliner (US-008.BOLT-001, SPEC-261124-1037 rev 2, ADR-046 §3.3):
/// the component that resolves one packaged, repository-relative reference against
/// the correct base, checks containment, reads it through
/// <see cref="ITemplatePackageFiles"/>, and emits the inlined CSS text or a
/// <c>data:</c> URI — owning the closed-list classification, the CSS
/// <c>@import</c>/<c>url()</c>/<c>@font-face</c> scan, cycle detection and the depth bound.
/// <para>
/// These oracles pin the two things best tested at the inliner's own surface: the
/// two resolution bases (package root for an HTML-element reference; the
/// referencing stylesheet's own directory for a CSS reference — SPEC §6 B.2) and
/// the <c>@import</c> depth/cycle rule (D5, depth 4). The negative "resolves
/// against the wrong base" cases prove the base, not merely the presence of the
/// asset. Every oracle derives from the approved intent, never from the
/// implementation (G06); the production types do not exist yet, so this is RED.
/// </para>
/// </summary>
public sealed class HtmlAssetInlinerTests
{
    // ---- Resolution base: HTML-element references resolve against the PACKAGE ROOT ----

    [Fact]
    public void AStylesheetLinkResolvesAgainstThePackageRoot()
    {
        var package = Package(("html/styles.css", "body{color:red}"));

        var outcome = new HtmlAssetInliner().InlineStylesheetLink("html/styles.css", package);

        Assert.True(outcome.Inlined);
        Assert.Null(outcome.Refusal);
        Assert.Contains("body{color:red}", outcome.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnElementAssetResolvesAgainstThePackageRoot()
    {
        var package = Package(("logo.png", "LOGO"));

        var outcome = new HtmlAssetInliner().InlineElementAsset("logo.png", package);

        Assert.True(outcome.Inlined);
        Assert.StartsWith("data:image/png;base64,", outcome.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnElementAssetDoesNotResolveAgainstAnySubdirectory()
    {
        // The asset sits only under html/, not at the root — an element reference
        // resolved against the package root must not find it.
        var package = Package(("html/logo.png", "LOGO"));

        var outcome = new HtmlAssetInliner().InlineElementAsset("logo.png", package);

        Assert.False(outcome.Inlined);
        Assert.Equal(HtmlRefusalCause.NonInlinableOrOffList, outcome.Refusal!.Cause);
    }

    [Fact]
    public void AnElementAssetDataUriDecodesToTheAssetBytes()
    {
        var package = Package(("logo.png", "PNGBYTES"));

        var outcome = new HtmlAssetInliner().InlineElementAsset("logo.png", package);

        Assert.True(outcome.Inlined);
        var base64 = outcome.Content!["data:image/png;base64,".Length..];
        Assert.Equal(Encoding.UTF8.GetBytes("PNGBYTES"), Convert.FromBase64String(base64));
    }

    // ---- Resolution base: CSS references resolve against the STYLESHEET'S OWN DIRECTORY ----

    [Fact]
    public void ACssUrlResolvesAgainstTheReferencingStylesheetDirectory()
    {
        var package = Package(
            ("html/styles.css", ".a{background:url(logo.png)}"),
            ("html/logo.png", "LOGO"));

        var outcome = new HtmlAssetInliner().InlineStylesheetLink("html/styles.css", package);

        Assert.True(outcome.Inlined);
        Assert.Contains("url(data:image/png;base64,", outcome.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("url(logo.png)", outcome.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void ACssUrlDoesNotResolveAgainstThePackageRoot()
    {
        // logo.png exists at the root but NOT beside the stylesheet (html/). A CSS url()
        // must resolve against the stylesheet's directory, so it is not found → refused.
        var package = Package(
            ("html/styles.css", ".a{background:url(logo.png)}"),
            ("logo.png", "LOGO"));

        var outcome = new HtmlAssetInliner().InlineStylesheetLink("html/styles.css", package);

        Assert.False(outcome.Inlined);
        Assert.Equal(HtmlRefusalCause.NonInlinableOrOffList, outcome.Refusal!.Cause);
    }

    // ---- @import depth bound (D5 = 4) and cycle detection ----

    [Fact]
    public void AnImportChainExactlyAtTheDepthBoundIsInlined()
    {
        // Depth = @import hops below the entry stylesheet. Exactly 4 hops inline.
        var package = Package(
            ("html/styles.css", "@import url(l1.css);"),
            ("html/l1.css", "@import url(l2.css);"),
            ("html/l2.css", "@import url(l3.css);"),
            ("html/l3.css", "@import url(l4.css);"),
            ("html/l4.css", ".leaf{color:green}"));

        var outcome = new HtmlAssetInliner(4).InlineStylesheetLink("html/styles.css", package);

        Assert.True(outcome.Inlined);
        Assert.Contains(".leaf{color:green}", outcome.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", outcome.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportChainOneBeyondTheDepthBoundIsRefused()
    {
        var package = Package(
            ("html/styles.css", "@import url(l1.css);"),
            ("html/l1.css", "@import url(l2.css);"),
            ("html/l2.css", "@import url(l3.css);"),
            ("html/l3.css", "@import url(l4.css);"),
            ("html/l4.css", "@import url(l5.css);"),
            ("html/l5.css", ".leaf{color:green}"));

        var outcome = new HtmlAssetInliner(4).InlineStylesheetLink("html/styles.css", package);

        Assert.False(outcome.Inlined);
        Assert.Equal(HtmlRefusalCause.ImportCycleOrDepth, outcome.Refusal!.Cause);
    }

    [Fact]
    public void ACyclicImportIsRefused()
    {
        var package = Package(
            ("html/styles.css", "@import url(a.css);"),
            ("html/a.css", "@import url(styles.css);"));

        var outcome = new HtmlAssetInliner().InlineStylesheetLink("html/styles.css", package);

        Assert.False(outcome.Inlined);
        Assert.Equal(HtmlRefusalCause.ImportCycleOrDepth, outcome.Refusal!.Cause);
    }

    // ---- outside-package references ----

    [Fact]
    public void AnExternalStylesheetLinkIsRefusedAsOutsideThePackage()
    {
        var outcome = new HtmlAssetInliner().InlineStylesheetLink("https://host.example/x.css", Package());

        Assert.False(outcome.Inlined);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, outcome.Refusal!.Cause);
    }

    [Theory]
    [InlineData("../secret.css")]
    [InlineData("html/../../secret.css")]
    [InlineData("%2e%2e/secret.css")]
    public void AStylesheetLinkThatEscapesThePackageRootIsRefusedBeforeReading(string escapingHref)
    {
        var package = Package((escapingHref, "LEAK"));

        var outcome = new HtmlAssetInliner().InlineStylesheetLink(escapingHref, package);

        Assert.False(outcome.Inlined);
        Assert.Equal(HtmlRefusalCause.TraversalEscape, outcome.Refusal!.Cause);
        Assert.Null(outcome.Content);
    }

    [Fact]
    public void TheDefaultMaxImportDepthIsFour()
    {
        Assert.Equal(4, new HtmlAssetInliner().MaxImportDepth);
        Assert.Equal(4, HtmlAssetInliner.DefaultMaxImportDepth);
    }

    private static MemoryPackageFiles Package(params (string Path, string Content)[] files) =>
        new(files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal));
}
