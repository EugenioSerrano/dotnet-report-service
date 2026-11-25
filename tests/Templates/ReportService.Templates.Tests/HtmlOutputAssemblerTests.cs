using System.Text;

namespace ReportService.Templates.Tests;

/// <summary>
/// The HTML output assembler (US-008.BOLT-001, SPEC-261124-1037 rev 2, ADR-046):
/// a bounded streaming write over the composed document-mode fragments that
/// resolves and inlines the template's packaged, repository-relative asset
/// references (the closed list of five kinds, confined to the non-repeated
/// regions) and otherwise refuses, producing no complete file.
/// <para>
/// Every oracle here derives from the approved intent (US-008 AC-2/AC-3/AC-7,
/// ADR-046 §3.1–§3.6, SPEC §6–§7), never from a production implementation — the
/// production types do not exist yet, so this suite is RED by construction (G06).
/// The fake package (<see cref="MemoryPackageFiles"/>) performs no containment of
/// its own, so a refusal on an out-of-package name proves the assembler's own
/// normalise-before-read containment, not the read surface's.
/// </para>
/// </summary>
public sealed class HtmlOutputAssemblerTests
{
    // ---- AC-A (US-008 AC-2): one self-contained .html on a non-empty snapshot ----

    [Fact]
    public async Task ANonEmptySnapshotWithAPackagedStylesheetLinkProducesOneSelfContainedFile()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<!DOCTYPE html><html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.Row, "<tr><td>1</td></tr>"),
            (FragmentKind.Row, "<tr><td>2</td></tr>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("html/styles.css", "body{color:red}"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Null(result.Refusal);
        // The stylesheet <link> is replaced by an inlined <style> carrying its text.
        Assert.Contains("<style>", output, StringComparison.Ordinal);
        Assert.Contains("body{color:red}", output, StringComparison.Ordinal);
        Assert.Contains("</style>", output, StringComparison.Ordinal);
        // No reference reaching outside the file remains.
        Assert.DoesNotContain("<link", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"html/styles.css\"", output, StringComparison.Ordinal);
        // The document-mode body is streamed in order, unchanged.
        Assert.Contains("<tr><td>1</td></tr><tr><td>2</td></tr>", output, StringComparison.Ordinal);
        Assert.EndsWith("</body></html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackagedImageReferenceIsInlinedAsADataUri()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><img src=\"logo.png\">"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "PNGBYTES"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("PNGBYTES")), output, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"logo.png\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackagedCssUrlReferenceIsInlinedAsADataUri()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        // url(...) resolves against the stylesheet's own directory (html/).
        var package = Package(
            ("html/styles.css", ".a{background:url(bg.png)}"),
            ("html/bg.png", "BGBYTES"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("url(data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.DoesNotContain("url(bg.png)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackagedFontFaceSourceIsInlinedAsADataUri()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(
            ("html/styles.css", "@font-face{font-family:x;src:url(x.woff2)}"),
            ("html/x.woff2", "FONTBYTES"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("src:url(data:", output, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("FONTBYTES")), output, StringComparison.Ordinal);
        Assert.DoesNotContain("url(x.woff2)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralDistinctPackagedAssetsAreAllInlinedInOnePass()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body><img src=\"logo.png\">"),
            (FragmentKind.Row, "<tr><td>r</td></tr>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(
            ("html/styles.css", ".a{background:url(bg.png)}@font-face{src:url(f.woff2)}"),
            ("html/bg.png", "BG"),
            ("html/f.woff2", "FONT"),
            ("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.DoesNotContain("<link", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(bg.png)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("url(f.woff2)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"logo.png\"", output, StringComparison.Ordinal);
        // Three binary assets are inlined as data:/base64 (logo.png via <img>, bg.png via CSS
        // url(), f.woff2 via @font-face); the stylesheet itself is inlined as <style> text, not
        // base64 (pinned by ANonEmptySnapshot…), so the base64 count is 3, not 4.
        Assert.Equal(3, CountOccurrences(output, "base64,"));
    }

    [Fact]
    public async Task NonResourceContentIsLeftUntouched()
    {
        // An <a href> navigation link and http(s) text in record data are not
        // resource-loading references (ADR-046 §3.3 item 4) — they are emitted unchanged.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><a href=\"https://example.com/page\">x</a>"),
            (FragmentKind.Row, "<tr><td>see https://data.example/x</td></tr>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package();

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("<a href=\"https://example.com/page\">x</a>", output, StringComparison.Ordinal);
        Assert.Contains("see https://data.example/x", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameInputProducesByteIdenticalOutputAcrossRuns()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body><img src=\"logo.png\">"),
            (FragmentKind.Row, "<tr><td>1</td></tr>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(
            ("html/styles.css", ".a{background:url(bg.png)}"),
            ("html/bg.png", "BG"),
            ("logo.png", "LOGO"));

        var first = await AssembleBytes(block, package);
        var second = await AssembleBytes(block, package);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1000)]
    public async Task InlinedAssetsAppearABoundedNumberOfTimesIndependentOfRecordCount(int records)
    {
        // A resource reference confined to the non-repeated regions is inlined once,
        // not once per record: the <style> and the data: URI counts do not grow with
        // the record count (ADR-046 §3.2 item 3, §3.3 item 2).
        var parts = new List<(FragmentKind, string)>
        {
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body><img src=\"logo.png\">"),
        };
        for (var i = 0; i < records; i++)
        {
            parts.Add((FragmentKind.Row, "<tr><td>r</td></tr>"));
        }

        parts.Add((FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("html/styles.css", "body{color:red}"), ("logo.png", "LOGO"));

        var (result, output) = await Assemble(Block([.. parts]), package);

        Assert.True(result.IsProduced);
        Assert.Equal(1, CountOccurrences(output, "<style>"));
        Assert.Equal(1, CountOccurrences(output, "base64,"));
        Assert.Equal(records, CountOccurrences(output, "<tr><td>r</td></tr>"));
    }

    // ---- AC-B (US-008 AC-3): header-only .html on an empty snapshot ----

    [Fact]
    public async Task AnEmptySnapshotProducesAHeaderOnlySelfContainedFileByTheSamePath()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("html/styles.css", "body{color:red}"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("<style>", output, StringComparison.Ordinal);
        Assert.Contains("body{color:red}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<tr", output, StringComparison.Ordinal);
        Assert.Contains("<body></body>", output, StringComparison.Ordinal);
    }

    // ---- AC-C (US-008 AC-7): refuse — no .html, a coarse cause ----

    [Fact]
    public async Task AnExternalStylesheetLinkIsRefusedAndNoCompleteFileIsProduced()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"https://host.example/x.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.NotNull(result.Refusal);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
        Assert.Contains("https://host.example/x.css", result.Refusal.Reference, StringComparison.Ordinal);
        // Refused assembly never yields a complete self-contained document.
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProtocolRelativeStylesheetLinkIsRefusedAsOutsideThePackage()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"//host.example/x.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task ARemoteImportInsideAStylesheetIsRefusedAsOutsideThePackage()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("html/styles.css", "@import url(https://host.example/x.css);"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task ARemoteCssUrlIsRefusedAsOutsideThePackage()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("html/styles.css", ".a{background:url(https://host.example/i.png)}"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AnExternalScriptSourceIsRefusedAsOutsideThePackage()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><script src=\"https://host.example/a.js\"></script></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Theory]
    // Every resource-loading reference kind off the closed list of five (ADR-046 §3.3 item 1).
    [InlineData("<img srcset=\"a.png 1x, b.png 2x\">")]
    [InlineData("<picture><source srcset=\"a.webp\"></picture>")]
    [InlineData("<video><source src=\"a.mp4\"></video>")]
    [InlineData("<svg><image href=\"a.png\"></image></svg>")]
    [InlineData("<svg><image xlink:href=\"a.png\"></image></svg>")]
    [InlineData("<link rel=\"icon\" href=\"a.ico\">")]
    [InlineData("<link rel=\"preload\" href=\"a.css\" as=\"style\">")]
    [InlineData("<iframe src=\"a.html\"></iframe>")]
    [InlineData("<object data=\"a.pdf\"></object>")]
    [InlineData("<embed src=\"a.swf\">")]
    [InlineData("<video><track src=\"a.vtt\"></video>")]
    [InlineData("<script src=\"app.js\"></script>")]
    public async Task AReferenceOffTheClosedInlinableListIsRefused(string offListMarkup)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>" + offListMarkup),
            (FragmentKind.DocumentEnd, "</body></html>"));
        // Even though every named asset exists in the package, an off-list kind is not inlined.
        var package = Package(
            ("a.png", "A"), ("b.png", "B"), ("a.webp", "A"), ("a.mp4", "A"), ("a.ico", "A"),
            ("a.css", "A"), ("a.html", "A"), ("a.pdf", "A"), ("a.swf", "A"), ("a.vtt", "A"), ("app.js", "A"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.NonInlinableOrOffList, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AnAbsentPackagedAssetIsRefusedAsNonInlinable()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><img src=\"missing.png\">"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.NonInlinableOrOffList, result.Refusal!.Cause);
    }

    [Theory]
    // A packaged-looking path that normalises outside the package root is refused BEFORE any
    // read — the fake package would return "LEAK" for each key, proving the assembler's own
    // normalise-before-containment check (SPEC §6 B.2, ADR-046 §3.3 item 3).
    [InlineData("../secret.css")]
    [InlineData("html/../../secret.css")]
    [InlineData("%2e%2e/secret.css")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\secret.css")]
    [InlineData("\\\\host\\share\\secret.css")]
    [InlineData("file:///etc/passwd")]
    public async Task AStylesheetLinkThatEscapesThePackageRootIsRefusedBeforeReading(string escapingHref)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"" + escapingHref + "\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        // A naive package that happily returns content for the escaping name.
        var package = Package((escapingHref, "LEAK{color:red}"));

        var (result, output) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.NotNull(result.Refusal);
        Assert.Contains(result.Refusal!.Cause, new[] { HtmlRefusalCause.TraversalEscape, HtmlRefusalCause.OutsidePackage });
        Assert.DoesNotContain("LEAK", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../secret.css")]
    [InlineData("html/../../secret.css")]
    [InlineData("%2e%2e/secret.css")]
    public async Task ARelativeTraversalEscapeIsClassifiedAsATraversalEscape(string escapingHref)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"" + escapingHref + "\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package((escapingHref, "LEAK"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.TraversalEscape, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AResourceReferenceInsideARepeatedRowFragmentIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>"),
            (FragmentKind.Row, "<tr><td><img src=\"logo.png\"></td></tr>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        // The asset exists; it is refused for its region, not its absence.
        var package = Package(("logo.png", "LOGO"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.RepeatedRegion, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AResourceReferenceInsideAGroupFragmentIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>"),
            (FragmentKind.GroupStart, "<h2><img src=\"logo.png\"></h2>"),
            (FragmentKind.Row, "<tr><td>r</td></tr>"),
            (FragmentKind.GroupEnd, "<p>end</p>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.RepeatedRegion, result.Refusal!.Cause);
    }

    [Fact]
    public async Task ACyclicImportChainIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(
            ("html/styles.css", "@import url(a.css);"),
            ("html/a.css", "@import url(styles.css);"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.ImportCycleOrDepth, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AnImportChainAtTheDepthBoundIsInlinedAndOneBeyondIsRefused()
    {
        // Depth = the number of @import hops below the entry stylesheet reached by the
        // <link>. The bound is 4: a chain of exactly 4 hops inlines; a 5th hop is refused.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var depth4 = Package(
            ("html/styles.css", "@import url(l1.css);"),
            ("html/l1.css", "@import url(l2.css);"),
            ("html/l2.css", "@import url(l3.css);"),
            ("html/l3.css", "@import url(l4.css);"),
            ("html/l4.css", ".leaf{color:green}"));
        var (produced, outputAt4) = await Assemble(block, depth4);
        Assert.True(produced.IsProduced);
        Assert.Contains(".leaf{color:green}", outputAt4, StringComparison.Ordinal);

        var depth5 = Package(
            ("html/styles.css", "@import url(l1.css);"),
            ("html/l1.css", "@import url(l2.css);"),
            ("html/l2.css", "@import url(l3.css);"),
            ("html/l3.css", "@import url(l4.css);"),
            ("html/l4.css", "@import url(l5.css);"),
            ("html/l5.css", ".leaf{color:green}"));
        var (refused, _) = await Assemble(block, depth5);
        Assert.False(refused.IsProduced);
        Assert.Equal(HtmlRefusalCause.ImportCycleOrDepth, refused.Refusal!.Cause);
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new HtmlOutputAssembler().AssembleAsync(block, Package(), destination, cts.Token));
    }

    // ---- V-Bounce 3: self-containment of CSS NOT in a packaged stylesheet file ----
    // ADR-046 §3.3 forbids ANY resource-loading reference reaching outside the file,
    // wherever it sits — an inline <style> block and a style="" attribute carry CSS
    // url()/@import exactly like a packaged stylesheet, and the closed inlinable list
    // and the forbidden/escape rules apply to them too (SPEC §6 B.1–B.3). A scanner
    // that looks only at element tag attributes lets these reach outside unrefused.

    [Fact]
    public async Task AnInlineStyleBlockWithARemoteUrlIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><style>.a{background:url(https://host.example/i.png)}</style></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInlineStyleBlockWithARemoteImportIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><style>@import url(https://host.example/x.css);</style></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AnInlineStyleBlockWithAPackageEscapingUrlIsRefusedBeforeReading()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><style>.a{background:url(../secret.png)}</style></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        // A naive package that would happily return content for the escaping name.
        var package = Package(("../secret.png", "LEAK"));

        var (result, output) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.TraversalEscape, result.Refusal!.Cause);
        Assert.DoesNotContain("LEAK", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInlineStyleBlockWithAPackagedRelativeUrlIsInlined()
    {
        // The inline <style> sits in the document, whose location is the package root,
        // so a relative url() resolves against the package root (SPEC §6 B.2).
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><style>.a{background:url(logo.png)}</style></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("url(data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.DoesNotContain("url(logo.png)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStyleAttributeWithARemoteUrlIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=\"background:url(https://host.example/i.png)\">"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStyleAttributeWithAPackagedRelativeUrlIsInlined()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=\"background:url(logo.png)\">"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("url(data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.DoesNotContain("url(logo.png)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStyleAttributeWithAPackageEscapingUrlIsRefusedBeforeReading()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=\"background:url(../secret.png)\">"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));
        var package = Package(("../secret.png", "LEAK"));

        var (result, output) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.TraversalEscape, result.Refusal!.Cause);
        Assert.DoesNotContain("LEAK", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStyleAttributeResourceReferenceInsideARepeatedRowIsRefused()
    {
        // Region confinement: a resource reference in a repeated part is refused even
        // when it is packaged and relative (ADR-046 §3.3 item 2).
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>"),
            (FragmentKind.Row, "<tr><td style=\"background:url(logo.png)\">r</td></tr>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.RepeatedRegion, result.Refusal!.Cause);
    }

    [Theory]
    // Legacy/other inline resource attributes carry a resource reference but are NOT on
    // the closed inlinable list of five, so a packaged-relative one is refused off-list
    // and a remote one is refused as outside the package (ADR-046 §3.3 item 1, §3.4).
    [InlineData("<body background=\"bg.png\">", HtmlRefusalCause.NonInlinableOrOffList)]
    [InlineData("<video poster=\"p.png\"></video>", HtmlRefusalCause.NonInlinableOrOffList)]
    [InlineData("<body background=\"https://host.example/bg.png\">", HtmlRefusalCause.OutsidePackage)]
    [InlineData("<video poster=\"https://host.example/p.png\"></video>", HtmlRefusalCause.OutsidePackage)]
    public async Task AnOffListInlineResourceAttributeIsRefused(string markup, HtmlRefusalCause expected)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>" + markup),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("bg.png", "BG"), ("p.png", "P"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(expected, result.Refusal!.Cause);
    }

    [Fact]
    public async Task ARemoteBaseHrefIsRefused()
    {
        // A <base href> to a remote origin would make every relative reference resolve
        // off-host — it cannot leave the output self-contained.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><base href=\"https://host.example/\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Contains(result.Refusal!.Cause, new[] { HtmlRefusalCause.OutsidePackage, HtmlRefusalCause.NonInlinableOrOffList });
    }

    [Theory]
    // A percent-encoded (or otherwise encoded) traversal is classified on its DECODED
    // value and REFUSED — never thrown — and refused before any package read (SPEC §6 B.2).
    [InlineData("..%5Csecret.css")]   // encoded backslash: ..\secret.css
    [InlineData("%2e%2e/secret.css")] // encoded ..
    [InlineData("%43%3A/x.css")]      // encoded C:/x.css
    public async Task AnEncodedTraversalReturnsARefusedResultNeverThrows(string escapingHref)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"" + escapingHref + "\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package((escapingHref, "LEAK"));

        using var destination = new MemoryStream();
        var exception = await Record.ExceptionAsync(() =>
            new HtmlOutputAssembler().AssembleAsync(block, package, destination, TestContext.Current.CancellationToken));
        var output = Encoding.UTF8.GetString(destination.ToArray());

        Assert.Null(exception);
        Assert.DoesNotContain("LEAK", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEncodedTraversalIsClassifiedAsARefusal()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"%2e%2e/secret.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("%2e%2e/secret.css", "LEAK"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Contains(result.Refusal!.Cause, new[] { HtmlRefusalCause.TraversalEscape, HtmlRefusalCause.OutsidePackage });
    }

    [Fact]
    public async Task AGreaterThanInsideAnEarlierAttributeDoesNotEndTagScanningPrematurely()
    {
        // A naive scanner that ends the tag at the first '>' would stop inside alt="a > b"
        // and miss the remote src — the tag must be scanned with attribute-quote awareness.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><img alt=\"a > b\" src=\"https://host.example/i.png\">"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AGreaterThanInsideAnEarlierAttributeStillAllowsAPackagedReferenceToBeInlined()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><img alt=\"x > y\" src=\"logo.png\">"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"logo.png\"", output, StringComparison.Ordinal);
    }

    // ---- V-Bounce 4: UNQUOTED attribute values (valid HTML5) must be read too ----
    // ADR-046 §3.3 item 4 / SPEC §10: an attacker-controlled template cannot make the
    // output fetch a remote/host resource. An attribute reader that matches only quoted
    // values leaves unquoted values invisible, so they pass unrefused/un-inlined.

    [Theory]
    [InlineData("<img src=https://host.example/x.png>")]
    [InlineData("<script src=https://host.example/a.js></script>")]
    [InlineData("<body background=https://host.example/bg.png>")]
    [InlineData("<video poster=https://host.example/p.png></video>")]
    public async Task AnUnquotedRemoteResourceAttributeIsRefused(string markup)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>" + markup),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnquotedRelativeImageIsInlined()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><img src=logo.png>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.DoesNotContain("src=logo.png", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnquotedStyleAttributeWithARemoteUrlIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=background:url(https://host.example/i.png)>"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AnUnquotedStyleAttributeWithARelativeUrlIsInlined()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=background:url(logo.png)>"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("url(data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.DoesNotContain("url(logo.png)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnquotedPackageEscapingValueIsRefusedBeforeReading()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><img src=../secret.png>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("../secret.png", "LEAK"));

        var (result, output) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.TraversalEscape, result.Refusal!.Cause);
        Assert.DoesNotContain("LEAK", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnquotedPackagedResourceAttributeInARepeatedRowIsRefused()
    {
        // Region confinement must apply to an unquoted value too (ADR-046 §3.3 item 2).
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>"),
            (FragmentKind.Row, "<tr><td><img src=logo.png></td></tr>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.RepeatedRegion, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AnUnquotedRemoteResourceAttributeInARepeatedRowIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>"),
            (FragmentKind.Row, "<tr><td><img src=https://host.example/x.png></td></tr>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Contains(result.Refusal!.Cause, new[] { HtmlRefusalCause.RepeatedRegion, HtmlRefusalCause.OutsidePackage });
    }

    [Fact]
    public async Task AnUnclosedStyleBlockCarryingARemoteUrlIsRefused()
    {
        // An unterminated <style> (no </style>) whose reference cannot be safely scoped
        // must not yield a file claimed self-contained — it is refused (SPEC §10 intent).
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><style>.a{background:url(https://host.example/i.png)}"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Contains(result.Refusal!.Cause, new[] { HtmlRefusalCause.OutsidePackage, HtmlRefusalCause.NonInlinableOrOffList });
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResourceLikeReferenceInsideAnHtmlCommentIsInert()
    {
        // Commented-out content is not a resource load: it must not cause a spurious
        // refusal nor be inlined — the comment is emitted as the composition produced it.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><!-- <img src=https://host.example/x.png> --></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.True(result.IsProduced);
        Assert.Contains("<!-- <img src=https://host.example/x.png> -->", output, StringComparison.Ordinal);
        // The commented reference was NOT inlined: no asset data: URI is present. (The
        // mandated CSP meta carries the substring "data:" in img-src/font-src, so the old
        // DoesNotContain("data:") is over-broad; an inlined asset always yields "base64,",
        // which the CSP meta never does — this asserts the true intent precisely.)
        Assert.DoesNotContain("base64,", output, StringComparison.Ordinal);
    }

    // ---- V-Bounce 5: CONVERGENCE — the complete realistic scan surface ----
    // ADR-046 §3.3 item 4 + SPEC §10: an attacker-controlled template cannot make the
    // assembled .html fetch a remote/host resource, and no reference may reach outside
    // the packaged file. The scanner must therefore see a live resource reference in
    // EVERY construct a browser would act on, after HTML-entity AND percent decoding,
    // in every region, case-insensitively, across all CSS reference forms.

    [Theory]
    // HTML entity / numeric-character-reference encoded values (decode before classify)
    [InlineData("<img src=&#104;ttps://host.example/x.png>")]
    [InlineData("<img src=\"&#x68;ttps://host.example/x.png\">")]
    // Inline SVG (foreign content): href / xlink:href / <use> / <svg><style>
    [InlineData("<svg><image href=\"https://host.example/x.png\"/></svg>")]
    [InlineData("<svg><image xlink:href=\"https://host.example/x.png\"/></svg>")]
    [InlineData("<svg><use href=\"https://host.example/x.svg#i\"/></svg>")]
    [InlineData("<svg><style>@import url(https://host.example/x.css);</style></svg>")]
    // CDATA inside SVG <style> carrying a remote reference
    [InlineData("<svg><style><![CDATA[@import url(https://host.example/y.css);]]></style></svg>")]
    // Protocol / edge forms: uppercase scheme, protocol-relative, whitespace in token
    [InlineData("<img src=\"HTTPS://host.example/x.png\">")]
    [InlineData("<img src=\"//host.example/x.png\">")]
    [InlineData("<div style=\"background:url(  https://host.example/x.png  )\">")]
    [InlineData("<div style=\"cursor:url(https://host.example/c.cur),auto\">")]
    // @import nested inside an at-rule block
    [InlineData("<style>@media screen{@import url(https://host.example/x.css);}</style>")]
    // Downlevel-REVEALED conditional comment: content a modern browser renders is live
    [InlineData("<!--[if !IE]>--><img src=\"https://host.example/x.png\"><!--<![endif]-->")]
    public async Task AnyConstructCarryingARemoteReferenceIsRefused(string markup)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>" + markup),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondRemoteUrlInACommaListIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=\"background:url(logo.png),url(https://host.example/b.png)\">"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task ARemoteUrlInAnImageSetIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=\"background:image-set(url(logo.png) 1x,url(https://host.example/b.png) 2x)\">"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task ARemoteFontFaceAlternateSourceIsRefused()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><style>@font-face{font-family:x;src:url(a.woff) format('woff'),url(https://host.example/b.woff2)}</style></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("a.woff", "AWOFF"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AnExternalMetaRefreshIsRefused()
    {
        // A meta refresh to an external URL navigates/fetches off-host when opened.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><meta http-equiv=\"refresh\" content=\"0;url=https://host.example/\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Contains(result.Refusal!.Cause, new[] { HtmlRefusalCause.OutsidePackage, HtmlRefusalCause.NonInlinableOrOffList });
    }

    [Fact]
    public async Task AnEntityEncodedTraversalIsDecodedAndRefusedBeforeReading()
    {
        // &#46;&#46; decodes to ".." — a package escape, classified after decoding.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"stylesheet\" href=\"&#46;&#46;/secret.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("../secret.css", "LEAK"));

        var (result, output) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.TraversalEscape, result.Refusal!.Cause);
        Assert.DoesNotContain("LEAK", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntityEncodedPackagedReferenceIsDecodedAndInlined()
    {
        // l&#111;go.png decodes to logo.png — leaving the entity would make the browser
        // fetch a file not present in the output, so it must be decoded and inlined.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><img src=\"l&#111;go.png\">"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.DoesNotContain("l&#111;go.png", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothPackagedUrlsInACommaListAreInlined()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=\"background:url(a.png),url(b.png)\">"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));
        var package = Package(("a.png", "A"), ("b.png", "B"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.DoesNotContain("url(a.png)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("url(b.png)", output, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(output, "data:image/png;base64,"));
    }

    [Fact]
    public async Task AUrlTokenWithInnerQuotesAndSpacesResolvesAPackagedReference()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=\"background:url( 'logo.png' )\">"),
            (FragmentKind.DocumentEnd, "</div></body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("url(data:image/png;base64,", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeadingAndTrailingWhitespaceInAUrlAttributeIsTrimmedAndInlined()
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><img src=\" logo.png \">"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("data:image/png;base64,", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADownlevelHiddenConditionalCommentStaysInert()
    {
        // <!--[if IE]> ... <![endif]--> is a real comment to a modern browser: inert.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><!--[if IE]><img src=\"https://host.example/x.png\"><![endif]--></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.True(result.IsProduced);
        Assert.Contains("<!--[if IE]><img src=\"https://host.example/x.png\"><![endif]-->", output, StringComparison.Ordinal);
        // Inert: not inlined. "base64," marks an inlined asset data: URI and never appears
        // in the mandated CSP meta (which carries "data:" in img-src/font-src), so this
        // asserts the intent without colliding with the CSP meta.
        Assert.DoesNotContain("base64,", output, StringComparison.Ordinal);
    }

    // ================================================================================
    // ---- V-Bounce 6 (SPEC rev 3, ADR-047): CSP meta emission + head conformance,
    //      by-attribute classification, decode-before-detect, refusal-cause mapping ----
    //
    // Every oracle below derives from the APPROVED INTENT (US-008 AC-2/AC-3/AC-7,
    // ADR-047 §3.3/§3.8, SPEC rev 3 §6 Phase B/C, §7 AC-D/AC-E/AC-F), never from a
    // production implementation — the rev-3 behaviour does not exist yet, so these
    // are RED by construction (G06). The isolated tester authored them before any
    // production change (strict test-first).
    //
    // The NEW coarse refusal cause `head-not-conformable` (SPEC §6 A.1) now exists in
    // production as HtmlRefusalCause.HeadNotConformable; the three head oracles
    // (body-before-head, duplicate-head, head-not-locatable) assert that exact cause.
    // ================================================================================

    // The fixed CSP meta the assembler MUST emit as the first child of <head> (ADR-047 §3.8).
    private const string CspMeta =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\">";

    // ---- AC-D (SPEC §6 C.1/C.2/C.3): one fixture per branch, each ONE expected outcome ----

    [Fact]
    public async Task HeadPresentInjectsTheCspMetaAsTheFirstChildOfHeadBeforeExistingHeadContent()
    {
        // C.1 case 1: <head> present, no rendered element before it → inject CSP first,
        // before the existing <meta charset> and <link> (which the inliner handles).
        var block = Block(
            (FragmentKind.DocumentStart, "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("html/styles.css", "body{color:red}"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        AssertCspIsFirstChildOfHead(output);
        AssertCharsetWithinFirst1024Bytes(output);
        // The CSP meta precedes the (existing) charset meta in the head.
        Assert.True(
            output.IndexOf(CspMeta, StringComparison.Ordinal) < output.IndexOf("<meta charset", StringComparison.Ordinal),
            "the CSP meta must precede the existing charset meta");
    }

    [Fact]
    public async Task HtmlPresentWithoutAHeadEstablishesAHeadAfterTheHtmlTagWithTheCspMetaFirst()
    {
        // C.1 case 2 (html present): <html> open tag, no <head> anywhere → establish a
        // <head> right after <html…> with the CSP meta first + a charset within 1024 bytes.
        var block = Block(
            (FragmentKind.DocumentStart, "<!DOCTYPE html><html><body><table><tr><td>x</td></tr></table>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.True(result.IsProduced);
        AssertCspIsFirstChildOfHead(output);
        AssertCharsetWithinFirst1024Bytes(output);
    }

    [Fact]
    public async Task ABareFragmentWithNoHeadAnywhereEstablishesAHeadAtContentStartWithTheCspMetaFirst()
    {
        // C.1 case 2 (bare fragment): NO <head> and NO <html> anywhere, rendered content →
        // establish a <head> at the very start of the content, CSP meta first. Fixture
        // invariant: the input carries no <head> substring (distinct from body-before-head).
        const string start = "<body><table><tr><td>x</td></tr></table></body>";
        Assert.DoesNotContain("<head", start, StringComparison.OrdinalIgnoreCase);
        var block = Block(
            (FragmentKind.DocumentStart, start),
            (FragmentKind.DocumentEnd, "</html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.True(result.IsProduced);
        AssertCspIsFirstChildOfHead(output);
        AssertCharsetWithinFirst1024Bytes(output);
    }

    [Fact]
    public async Task AnUppercaseHtmlAndHeadAreHandledCaseInsensitivelyAndTheCspMetaIsInjectedFirst()
    {
        // C.1: tag matching is ASCII case-insensitive — an uppercase twin injects identically.
        var block = Block(
            (FragmentKind.DocumentStart, "<!DOCTYPE HTML><HTML><HEAD><meta charset=\"utf-8\"></HEAD><BODY>"),
            (FragmentKind.DocumentEnd, "</BODY></HTML>"));

        var (result, output) = await Assemble(block, Package());

        Assert.True(result.IsProduced);
        AssertCspIsFirstChildOfHead(output);
        AssertCharsetWithinFirst1024Bytes(output);
    }

    [Fact]
    public async Task AnEmptySnapshotCarriesTheCspMetaAsTheFirstChildOfHead()
    {
        // AC-B + AC-D: the empty-snapshot path also emits the CSP meta first in head.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("html/styles.css", "body{color:red}"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        AssertCspIsFirstChildOfHead(output);
    }

    [Fact]
    public async Task ATemplateThatAlreadySuppliesACspMetaStillGetsTheAssemblersOwnCspFirstAndKeepsTheTemplates()
    {
        // C.3: a template CSP meta already present → the assembler's OWN fixed CSP is the
        // first child of <head>; the template's own CSP meta is left in place unmodified.
        var block = Block(
            (FragmentKind.DocumentStart, "<!DOCTYPE html><html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'\"><meta charset=\"utf-8\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.True(result.IsProduced);
        AssertCspIsFirstChildOfHead(output);
        // The template's own (weaker) CSP meta is left in place, after the assembler's.
        Assert.Contains("content=\"default-src 'self'\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BodyBeforeHeadIsRefusedBecauseTheCspMetaCannotCoverThePrecedingRenderedContent()
    {
        // C.2: a <head> EXISTS but a rendered body element precedes it → refuse. Fixture
        // invariant: a <head> is present AND a rendered element precedes it (distinct from
        // the bare-fragment establish case, which has no <head> at all).
        const string start = "<table><tr><td>x</td></tr></table><head><meta charset=\"utf-8\"></head>";
        Assert.Contains("<head", start, StringComparison.OrdinalIgnoreCase);
        var block = Block(
            (FragmentKind.DocumentStart, start),
            (FragmentKind.DocumentEnd, "<body></body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.HeadNotConformable, result.Refusal!.Cause);
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicateHeadIsRefusedAsAmbiguousFirstInHeadPlacement()
    {
        // C.2: more than one <head> open tag in the scanned window → refuse.
        var block = Block(
            (FragmentKind.DocumentStart, "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><head></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.HeadNotConformable, result.Refusal!.Cause);
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadNotLocatableWithinTheBoundedScanIsRefused()
    {
        // C.2 scan-limit branch: >= 64 KiB of only comments/whitespace, no <head>, no
        // <html>, no rendered body element → no anchor found within the bounded scan → refuse.
        var filler = new string('x', 70 * 1024); // > 64 KiB inside a single comment, no anchor
        var block = Block(
            (FragmentKind.DocumentStart, "<!-- " + filler + " -->"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.HeadNotConformable, result.Refusal!.Cause);
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    // ---- AC-E (SPEC §6 B.4(a)): classify by resource-loading ATTRIBUTE, not element name ----

    [Theory]
    // A PACKAGED-RELATIVE resource-loading attribute on an un-enumerated element. Under a
    // by-ELEMENT-NAME floor an unknown element's relative reference is not recognised as a
    // resource load, so it slips through un-refused (produced) — RED. Intent: refuse
    // off-list by attribute. Every named asset EXISTS in the package, so the refusal is for
    // being off the closed inlinable list of five, not for absence.
    [InlineData("<video src=\"clip.mp4\"></video>", "clip.mp4")]
    [InlineData("<audio src=\"snd.mp3\"></audio>", "snd.mp3")]
    [InlineData("<source src=\"clip.mp4\">", "clip.mp4")]
    [InlineData("<input type=\"image\" src=\"btn.png\">", "btn.png")]
    [InlineData("<frame src=\"f.html\">", "f.html")]
    [InlineData("<table background=\"bg.png\"></table>", "bg.png")]
    [InlineData("<td background=\"bg.png\"></td>", "bg.png")]
    [InlineData("<th background=\"bg.png\"></th>", "bg.png")]
    public async Task APackagedRelativeResourceAttributeOnAnUnenumeratedElementIsRefusedByAttributeOffList(
        string markup, string packagedName)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>" + markup),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package((packagedName, "ASSET"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.NonInlinableOrOffList, result.Refusal!.Cause);
    }

    [Theory]
    // A REMOTE resource-loading attribute on an un-enumerated element → classified by
    // attribute, outside the package → refuse OutsidePackage.
    [InlineData("<video src=\"https://host.example/clip.mp4\"></video>")]
    [InlineData("<audio src=\"https://host.example/snd.mp3\"></audio>")]
    [InlineData("<input type=\"image\" src=\"https://host.example/btn.png\">")]
    [InlineData("<frame src=\"https://host.example/f.html\">")]
    [InlineData("<table background=\"https://host.example/bg.png\"></table>")]
    [InlineData("<td background=\"https://host.example/bg.png\"></td>")]
    [InlineData("<th background=\"https://host.example/bg.png\"></th>")]
    public async Task ARemoteResourceAttributeOnAnUnenumeratedElementIsRefusedByAttributeOutsidePackage(string markup)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>" + markup),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
    }

    [Fact]
    public async Task AByAttributeResourceReferenceInsideARepeatedRowIsRefusedForItsRegion()
    {
        // Region confinement by attribute: a packaged-relative resource attribute on an
        // un-enumerated element inside a repeated Row is refused for its region.
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><table>"),
            (FragmentKind.Row, "<tr><td background=\"bg.png\">r</td></tr>"),
            (FragmentKind.DocumentEnd, "</table></body></html>"));
        var package = Package(("bg.png", "BG"));

        var (result, _) = await Assemble(block, package);

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.RepeatedRegion, result.Refusal!.Cause);
    }

    // ---- AC-F (SPEC §6 B.4(b)): decode HTML entities / percent-encoding BEFORE detect ----

    [Theory]
    // The decided-on attribute value is decoded before detection, so an encoded reference
    // is detected and refused exactly as its decoded form (remote → OutsidePackage).
    // &#104; / %68 both decode to 'h' → 'http(s)://host.example/…'.
    [InlineData("<div style=\"background:url(&#104;ttps://host.example/x.png)\"></div>")]
    [InlineData("<div style=\"background:url(%68ttps://host.example/x.png)\"></div>")]
    public async Task AnEncodedRemoteReferenceInAStyleAttributeIsDecodedBeforeDetectionAndRefusedOutsidePackage(string markup)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>" + markup),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, output) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
        Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
    }

    [Theory]
    // The attribute that DECIDES a reference exists (http-equiv, content, rel) is decoded
    // before the floor detects it — an encoded http-equiv="refresh" / rel / content that
    // points off-host is detected after decode and refused, not slipped through.
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=&#104;ttps://host.example/\">")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=%68ttps://host.example/\">")]
    public async Task AnEncodedExternalMetaRefreshIsDecodedBeforeDetectionAndRefused(string markup)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head>" + markup + "</head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Contains(result.Refusal!.Cause, new[] { HtmlRefusalCause.OutsidePackage, HtmlRefusalCause.NonInlinableOrOffList });
    }

    [Fact]
    public async Task AnEncodedPackagedRelativeReferenceInAStyleAttributeIsDecodedBeforeDetectionAndInlined()
    {
        // The positive decode case: l&#111;go.png decodes to logo.png, a packaged relative
        // reference → inlined as a data: URI (not left encoded, not refused as absent).
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body><div style=\"background:url(l&#111;go.png)\"></div>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("logo.png", "LOGO"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("url(data:image/png;base64,", output, StringComparison.Ordinal);
        Assert.DoesNotContain("l&#111;go.png", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEncodedRelValueThatDecodesToStylesheetIsDecodedBeforeDetectionAndInlined()
    {
        // Decode-before-detect on the DECIDING attribute `rel` (SPEC §6 B.4b, ADR-047 §3.3
        // item 5): rel="&#115;tylesheet" decodes to rel="stylesheet" — a browser decodes the
        // attribute VALUE and treats it as a stylesheet link, so the floor must decode the
        // rel value before detecting "stylesheet" and inline the packaged relative sheet.
        // (Only the attribute VALUE is decoded — HTML parsers do not decode attribute NAMES,
        // so an encoded attribute-name is intentionally NOT treated as that attribute.)
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head><link rel=\"&#115;tylesheet\" href=\"html/styles.css\"></head><body>"),
            (FragmentKind.DocumentEnd, "</body></html>"));
        var package = Package(("html/styles.css", "body{color:red}"));

        var (result, output) = await Assemble(block, package);

        Assert.True(result.IsProduced);
        Assert.Contains("<style>", output, StringComparison.Ordinal);
        Assert.Contains("body{color:red}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Refusal-cause mapping (SPEC §6 B.1): off-list residuals → non-inlinable-or-off-list ----

    [Theory]
    // <meta http-equiv=refresh> (no external url), <a ping>, and inline <script> are the
    // residuals the CSP alone does not block; off the closed list → non-inlinable-or-off-list.
    [InlineData("<meta http-equiv=\"refresh\" content=\"5\">")]
    [InlineData("<a ping=\"track.gif\">x</a>")]
    [InlineData("<script>var a = 1;</script>")]
    public async Task AnOffListResidualReferenceMapsToTheNonInlinableOrOffListCause(string markup)
    {
        var block = Block(
            (FragmentKind.DocumentStart, "<html><head></head><body>" + markup),
            (FragmentKind.DocumentEnd, "</body></html>"));

        var (result, _) = await Assemble(block, Package());

        Assert.False(result.IsProduced);
        Assert.Equal(HtmlRefusalCause.NonInlinableOrOffList, result.Refusal!.Cause);
    }

    // ---- CSP head-conformance assertion helpers ----

    private static void AssertCspIsFirstChildOfHead(string output)
    {
        var headOpen = output.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        Assert.True(headOpen >= 0, "no <head> open tag present in the produced output");
        var headTagEnd = output.IndexOf('>', headOpen);
        Assert.True(headTagEnd > headOpen, "the <head> open tag is not closed");
        var afterHead = output[(headTagEnd + 1)..];
        Assert.StartsWith(CspMeta, afterHead, StringComparison.Ordinal);
    }

    private static void AssertCharsetWithinFirst1024Bytes(string output)
    {
        var idx = output.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
        Assert.True(idx >= 0, "no <meta charset> present in the produced output");
        var byteOffset = Encoding.UTF8.GetByteCount(output[..idx]);
        Assert.True(byteOffset < 1024, $"<meta charset> starts at byte {byteOffset}, not within the first 1024 bytes");
    }

    // ---- helpers ----

    private static async Task<(HtmlAssemblyResult Result, string Output)> Assemble(
        ComposedBlock block,
        ITemplatePackageFiles package,
        int maxImportDepth = HtmlOutputAssembler.DefaultMaxImportDepth)
    {
        using var destination = new MemoryStream();
        var result = await new HtmlOutputAssembler(maxImportDepth)
            .AssembleAsync(block, package, destination, TestContext.Current.CancellationToken);
        return (result, Encoding.UTF8.GetString(destination.ToArray()));
    }

    private static async Task<byte[]> AssembleBytes(ComposedBlock block, ITemplatePackageFiles package)
    {
        using var destination = new MemoryStream();
        await new HtmlOutputAssembler().AssembleAsync(block, package, destination, TestContext.Current.CancellationToken);
        return destination.ToArray();
    }

    private static ComposedBlock Block(params (FragmentKind Kind, string Text)[] parts)
    {
        var bytes = new List<byte>();
        var fragments = new List<ComposedFragment>(parts.Length);
        long rowOrdinal = 0;
        foreach (var (kind, text) in parts)
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            var start = bytes.Count;
            bytes.AddRange(encoded);
            var ordinal = kind == FragmentKind.Row ? ++rowOrdinal : 0;
            fragments.Add(new ComposedFragment(kind, start, encoded.Length, ordinal));
        }

        return new ComposedBlock([.. bytes], fragments);
    }

    private static MemoryPackageFiles Package(params (string Path, string Content)[] files) =>
        new(files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal));

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }
}
