using System.Globalization;
using System.Text;
using System.Text.Json;
using ReportService.Templates;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The HTML output assembler across the rendering boundary (US-008.BOLT-001,
/// SPEC-261124-1037 rev 2 §8.2, ADR-046): the real <see cref="BlockComposer"/>
/// drives a pinned real template package into a document-mode
/// <see cref="ComposedBlock"/>, and the real <see cref="HtmlOutputAssembler"/>
/// assembles it over the same real packaged asset set
/// (<see cref="FolderTemplatePackageFiles"/>).
/// <para>
/// This class is authored in the V-Bounce and <b>runs in the integration test
/// cycle</b> (Bolt <c>test_integration: required</c>), not in the hermetic
/// V-Bounce suite. Here it must compile and pass static checks. Its oracles come
/// from the approved intent (US-008 AC-2/AC-3/AC-7, ADR-046), never from the
/// implementation (G06). The non-inlinable-reference template is synthesized at
/// run time from the committed <c>pilot-simple</c> sample into a temporary folder,
/// so no tracked fixture file is added to the repository.
/// </para>
/// </summary>
public sealed class HtmlAssemblyCompositionTests
{
    private const string Sample = "pilot-simple";

    private static readonly DateTimeOffset Clock = DateTimeOffset.UnixEpoch;

    // AC-2: a pilot-shape template (relative stylesheet <link>) + a non-empty snapshot
    // → one self-contained .html whose stylesheet is inlined.
    [Fact]
    public async Task ANonEmptySnapshotOverARealPilotTemplateProducesOneSelfContainedFile()
    {
        var folder = SampleFolder();
        var block = await ComposeAsync(folder, RecordCount: 5);

        var (result, output) = await Assemble(block, folder);

        Assert.True(result.IsProduced);
        Assert.Contains("<style>", output, StringComparison.Ordinal);
        // The pilot stylesheet's own rules are inlined.
        Assert.Contains("table.items", output, StringComparison.Ordinal);
        // No reference reaching outside the file remains.
        Assert.DoesNotContain("<link", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"html/styles.css\"", output, StringComparison.Ordinal);
        // The document-mode body is present.
        Assert.Contains("<tr>", output, StringComparison.Ordinal);
        Assert.EndsWith("</html>\n", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // AC-3: an empty snapshot → a header-only self-contained .html by the same path.
    [Fact]
    public async Task AnEmptySnapshotOverARealPilotTemplateProducesAHeaderOnlyFile()
    {
        var folder = SampleFolder();
        var block = await ComposeAsync(folder, RecordCount: 0);

        var (result, output) = await Assemble(block, folder);

        Assert.True(result.IsProduced);
        Assert.Contains("<style>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", output, StringComparison.OrdinalIgnoreCase);
        // No record rows between the table body tags.
        Assert.DoesNotContain("<td class=\"num\">", output, StringComparison.Ordinal);
    }

    // AC-7: a template whose HTML references an external/remote asset → refused, nothing produced.
    [Fact]
    public async Task ATemplateWithAnExternalReferenceIsRefused()
    {
        var folder = SynthesizeTemplateWithExternalReference();
        try
        {
            var block = await ComposeAsync(folder, RecordCount: 3);

            var (result, output) = await Assemble(block, folder);

            Assert.False(result.IsProduced);
            Assert.NotNull(result.Refusal);
            Assert.Equal(HtmlRefusalCause.OutsidePackage, result.Refusal!.Cause);
            Assert.DoesNotContain("</html>", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- V-Bounce 6 (SPEC rev 3, ADR-047 §3.8): CSP meta + head conformance across the
    //      REAL composition→assembly pipeline. Oracles from approved intent, not the impl. ----

    // The fixed CSP meta the assembler MUST emit as the first child of <head> (ADR-047 §3.8).
    private const string CspMeta =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\">";

    // AC-D: the non-empty pilot output carries the CSP meta as the FIRST child of <head>,
    // before the pilot's own head content, with <meta charset> within the first 1024 bytes,
    // and the packaged stylesheet still inlined to <style>.
    [Fact]
    public async Task ANonEmptyPilotOutputCarriesTheCspMetaFirstInHeadAndStillInlinesTheStylesheet()
    {
        var folder = SampleFolder();
        var block = await ComposeAsync(folder, RecordCount: 5);

        var (result, output) = await Assemble(block, folder);

        Assert.True(result.IsProduced);
        AssertCspIsFirstChildOfHead(output);
        AssertCharsetWithinFirst1024Bytes(output);
        // The CSP meta precedes the pilot's own head content (charset, title, the link/style).
        Assert.True(
            output.IndexOf(CspMeta, StringComparison.Ordinal) < output.IndexOf("<meta charset", StringComparison.Ordinal),
            "the CSP meta must precede the pilot's own <meta charset>");
        Assert.True(
            output.IndexOf(CspMeta, StringComparison.Ordinal) < output.IndexOf("<title", StringComparison.Ordinal),
            "the CSP meta must precede the pilot's <title>");
        // The packaged stylesheet is still inlined (rev-2 behaviour preserved).
        Assert.Contains("<style>", output, StringComparison.Ordinal);
        Assert.Contains("table.items", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", output, StringComparison.OrdinalIgnoreCase);
    }

    // AC-D: the empty-snapshot pilot output also carries the CSP meta first in head.
    [Fact]
    public async Task AnEmptyPilotOutputCarriesTheCspMetaFirstInHead()
    {
        var folder = SampleFolder();
        var block = await ComposeAsync(folder, RecordCount: 0);

        var (result, output) = await Assemble(block, folder);

        Assert.True(result.IsProduced);
        AssertCspIsFirstChildOfHead(output);
        AssertCharsetWithinFirst1024Bytes(output);
        Assert.Contains("<style>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", output, StringComparison.OrdinalIgnoreCase);
    }

    // AC-D (ADR-047 §3.8 item 3): a pilot copy whose documentStart has NO <head> (but has
    // <html>) → the assembler ESTABLISHES a conforming head with the CSP meta first, produced.
    [Fact]
    public async Task APilotTemplateWithNoHeadHasAConformingHeadEstablishedWithTheCspMetaFirst()
    {
        var folder = SynthesizeTemplateWithNoHead();
        try
        {
            var block = await ComposeAsync(folder, RecordCount: 3);

            var (result, output) = await Assemble(block, folder);

            Assert.True(result.IsProduced);
            AssertCspIsFirstChildOfHead(output);
            AssertCharsetWithinFirst1024Bytes(output);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // AC-D (SPEC §6 C.3): a pilot copy whose documentStart already supplies its own CSP meta
    // → the assembler still injects its OWN CSP as the first child; the template's is left in
    // place, and the output is produced.
    [Fact]
    public async Task APilotTemplateThatSuppliesItsOwnCspStillGetsTheAssemblersCspFirst()
    {
        var folder = SynthesizeTemplateWithOwnCsp();
        try
        {
            var block = await ComposeAsync(folder, RecordCount: 3);

            var (result, output) = await Assemble(block, folder);

            Assert.True(result.IsProduced);
            AssertCspIsFirstChildOfHead(output);
            // The template's own (weaker) CSP meta is preserved, after the assembler's.
            var ownCsp = output.IndexOf(CspMeta, StringComparison.Ordinal);
            var templateCsp = output.IndexOf("content=\"default-src 'self'\"", StringComparison.Ordinal);
            Assert.True(templateCsp >= 0, "the template's own CSP meta must be left in place");
            Assert.True(ownCsp < templateCsp, "the assembler's CSP must precede the template's own CSP");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void AssertCspIsFirstChildOfHead(string output)
    {
        var headOpen = IndexOfHeadOpenTag(output);
        Assert.True(headOpen >= 0, "no <head> open tag present in the produced output");
        var headTagEnd = output.IndexOf('>', headOpen);
        Assert.True(headTagEnd > headOpen, "the <head> open tag is not closed");
        var afterHead = output[(headTagEnd + 1)..].TrimStart();
        Assert.StartsWith(CspMeta, afterHead, StringComparison.Ordinal);
    }

    // The index of a real <head> open tag (<head> or <head …>), not the <header> element.
    private static int IndexOfHeadOpenTag(string s)
    {
        var from = 0;
        while (true)
        {
            var i = s.IndexOf("<head", from, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                return -1;
            }

            var next = i + 5 < s.Length ? s[i + 5] : '\0';
            if (next is '>' or ' ' or '\t' or '\r' or '\n' or '/')
            {
                return i;
            }

            from = i + 5;
        }
    }

    private static void AssertCharsetWithinFirst1024Bytes(string output)
    {
        var idx = output.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
        Assert.True(idx >= 0, "no <meta charset> present in the produced output");
        var byteOffset = Encoding.UTF8.GetByteCount(output[..idx]);
        Assert.True(byteOffset < 1024, $"<meta charset> starts at byte {byteOffset}, not within the first 1024 bytes");
    }

    private static async Task<(HtmlAssemblyResult Result, string Output)> Assemble(ComposedBlock block, string folder)
    {
        using var destination = new MemoryStream();
        var result = await new HtmlOutputAssembler().AssembleAsync(
            block,
            new FolderTemplatePackageFiles(folder),
            destination,
            TestContext.Current.CancellationToken);
        return (result, Encoding.UTF8.GetString(destination.ToArray()));
    }

    private static async Task<ComposedBlock> ComposeAsync(string folder, int RecordCount)
    {
        var files = new FolderTemplatePackageFiles(folder);
        var manifest = new RenderManifest(
            ScribanTemplateAdapter.EngineName,
            ScribanTemplateAdapter.EngineVersion,
            new Dictionary<TemplatePart, string>
            {
                [TemplatePart.DocumentStart] = "html/document-start.html.sbn",
                [TemplatePart.Row] = "html/row.html.sbn",
                [TemplatePart.DocumentEnd] = "html/document-end.html.sbn",
            },
            ["html/styles.css"],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
            GroupKey: null);

        var adapter = new ScribanTemplateAdapter();
        var template = adapter.Prepare(manifest, files);

        var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleFolder(), "sample.json")));
        var header = RenderContextFactory.Header(
            Encoding.UTF8.GetBytes(sample.RootElement.GetProperty("header").GetRawText()));
        var items = sample.RootElement.GetProperty("items").EnumerateArray().Take(RecordCount).ToArray();
        var records = items
            .Select((item, index) => new ComposedRecord(index + 1, RenderContextFactory.Header(Encoding.UTF8.GetBytes(item.GetRawText()))))
            .ToArray();

        var settings = new PresentationSettings(CultureInfo.GetCultureInfo("es-AR"), TimeZoneInfo.Utc, Clock);
        var composer = new BlockComposer(new ManualTimeProvider(Clock));
        return await composer.ComposeAsync(
            new CompositionRequest(
                template,
                new CompositionBlock(0, records.Length, records.Length == 0 ? 1 : records[0].Ordinal, IsFirst: true, IsLast: true),
                header,
                records,
                new CompositionRender("es-AR", "America/Argentina/Buenos_Aires", Clock),
                GroupKey: null),
            adapter,
            settings,
            TestContext.Current.CancellationToken);
    }

    private static string SampleFolder() => Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample);

    // A copy of the committed pilot-simple package whose documentStart links an external
    // stylesheet in addition to its packaged one — the non-inlinable-reference case.
    private static string SynthesizeTemplateWithExternalReference()
    {
        var source = SampleFolder();
        var target = Path.Combine(Path.GetTempPath(), "report-service-html-external-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var documentStart = Path.Combine(target, "html", "document-start.html.sbn");
        var text = File.ReadAllText(documentStart)
            .Replace(
                "<link rel=\"stylesheet\" href=\"html/styles.css\">",
                "<link rel=\"stylesheet\" href=\"html/styles.css\">\n  <link rel=\"stylesheet\" href=\"https://host.example/remote.css\">",
                StringComparison.Ordinal);
        File.WriteAllText(documentStart, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return target;
    }

    // A copy of pilot-simple whose documentStart has its entire <head>…</head> removed (the
    // <html lang="es-AR"> open tag remains) — exercises the head-establishment path (ADR-047
    // §3.8 item 3). The packaged stylesheet <link> lived in the head, so it is removed too.
    private static string SynthesizeTemplateWithNoHead()
    {
        var target = CopyPilotToTemp("report-service-html-nohead-");
        var documentStart = Path.Combine(target, "html", "document-start.html.sbn");
        var text = File.ReadAllText(documentStart);
        var start = text.IndexOf("<head>", StringComparison.Ordinal);
        var end = text.IndexOf("</head>", StringComparison.Ordinal) + "</head>".Length;
        Assert.True(start >= 0 && end > start, "the pilot documentStart must carry a <head> to remove");
        text = text.Remove(start, end - start);
        // Precise invariant: no <head> tag remains. (Do not match "<head" loosely — the
        // pilot body carries <header class="document-header">, which is NOT a head tag.)
        Assert.True(IndexOfHeadOpenTag(text) < 0, "no <head> open tag may remain after removal");
        File.WriteAllText(documentStart, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return target;
    }

    // A copy of pilot-simple whose documentStart already supplies its own CSP <meta> inside
    // the head — exercises SPEC §6 C.3 (the assembler still injects its own CSP first).
    private static string SynthesizeTemplateWithOwnCsp()
    {
        var target = CopyPilotToTemp("report-service-html-owncsp-");
        var documentStart = Path.Combine(target, "html", "document-start.html.sbn");
        var text = File.ReadAllText(documentStart)
            .Replace(
                "<meta charset=\"utf-8\">",
                "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'\">\n  <meta charset=\"utf-8\">",
                StringComparison.Ordinal);
        File.WriteAllText(documentStart, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return target;
    }

    private static string CopyPilotToTemp(string prefix)
    {
        var source = SampleFolder();
        var target = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }
}
