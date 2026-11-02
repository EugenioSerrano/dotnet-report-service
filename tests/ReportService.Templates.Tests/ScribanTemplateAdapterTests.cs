using System.Text;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The Scriban adapter: the product's escaping policy on every data output, the
/// limits of the sandbox, includes confined to the package, a fresh scope per part,
/// and the numbers of the context printed as they were given.
/// </summary>
public sealed class ScribanTemplateAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryDataOutputIsEncodedWithTheProductPolicy()
    {
        var block = Compose(
            [(TemplatePart.DocumentStart, "<p>{{ header.name | html.escape }}</p>")],
            header: """{"name":"<a & \"b\" 'c' \u00e9\u00a0>"}""",
            records: []);

        Assert.Contains("&lt;a &amp; &quot;b&quot; &#39;c&#39; &#233;&#160;&gt;", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullValuePrintsAsEmpty()
    {
        var block = Compose([(TemplatePart.DocumentStart, "<p>{{ header.name | html.escape }}</p>")], header: """{"name":null}""", records: []);

        Assert.Equal("<p></p>", block.Text.Trim());
    }

    [Fact]
    public void TheNumbersOfTheContextPrintAsTheyWereGiven()
    {
        var block = Compose(
            [(TemplatePart.Row, "<tr><td>{{ item.unitPrice }}</td><td>{{ item.quantity }}</td></tr>")],
            header: "{}",
            records: ["""{"unitPrice":1000.00,"quantity":3}"""]);

        Assert.Contains("<td>1000.00</td><td>3</td>", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentPartSeesNoRecord()
    {
        var block = Compose(
            [(TemplatePart.DocumentStart, "<p>[{{ item.sequence }}]</p>")],
            header: "{}",
            records: ["""{"sequence":5}"""]);

        Assert.Equal("<p>[]</p>", block.Text.Trim());
    }

    [Fact]
    public void NothingAPartAssignsReachesTheNextPart()
    {
        var block = Compose(
            [(TemplatePart.DocumentStart, "{% x = 5 %}"), (TemplatePart.Row, "<tr><td>{{ x }}</td></tr>")],
            header: "{}",
            records: ["""{"sequence":1}"""]);

        Assert.Equal("<tr><td></td></tr>", block.TextOf(block.Rows.Single()));
    }

    [Theory]
    [InlineData("{{ 1 + }}")]
    [InlineData("{{ (( }}")]
    public void APartThatCannotBeParsedIsRefusedWhenThePackageIsPrepared(string source)
    {
        Assert.Throws<TemplatePackageException>(() => Prepare([(TemplatePart.DocumentStart, source)]));
    }

    [Fact]
    public void APackageOfAnotherEngineIsRefused()
    {
        var refusal = Assert.Throws<TemplatePackageException>(() => Prepare([(TemplatePart.DocumentStart, "<p></p>")], engine: "handlebars"));

        Assert.Contains("handlebars", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEngineKeepsTheLimitsThisIncrementFixes()
    {
        var adapter = new ScribanTemplateAdapter();

        Assert.Equal("scriban", adapter.Engine);
        Assert.Equal("7.5.1", ScribanTemplateAdapter.PinnedVersion);
        Assert.Equal(10_000, ScribanTemplateAdapter.LoopLimit);
        Assert.Equal(100, ScribanTemplateAdapter.RecursiveLimit);
        Assert.Equal(1024 * 1024, ScribanTemplateAdapter.LimitToString);
        Assert.Equal(TimeSpan.FromSeconds(1), ScribanTemplateAdapter.RegexTimeOut);
    }

    private static ComposedBlock Compose(
        (TemplatePart Part, string Source)[] parts,
        string header,
        string[] records,
        string? groupKey = null)
    {
        var template = Prepare(parts);
        var composer = new BlockComposer(new ScribanTemplateAdapter(), new ManualTimeProvider(Now));
        return composer.Compose(new CompositionRequest(
            template,
            new CompositionBlock(0, records.Length, 1, IsFirst: true, IsLast: true),
            RenderContextFactory.Header(Encoding.UTF8.GetBytes(header)),
            [.. records.Select((json, index) => new ComposedRecord(index + 1, RenderContextFactory.Header(Encoding.UTF8.GetBytes(json))))],
            new CompositionRender("en", "UTC", DateTimeOffset.UnixEpoch),
            groupKey));
    }

    private static PreparedTemplate Prepare((TemplatePart Part, string Source)[] parts, string engine = ScribanTemplateAdapter.EngineName)
    {
        var manifest = new RenderManifest(
            engine,
            ScribanTemplateAdapter.PinnedVersion,
            parts.ToDictionary(part => part.Part, part => part.Part.ToString(), EqualityComparer<TemplatePart>.Default),
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("en", "UTC"),
            GroupKey: null);
        var files = new MemoryPackageFiles(parts.ToDictionary(part => part.Part.ToString(), part => part.Source, StringComparer.Ordinal));
        return new ScribanTemplateAdapter().Prepare(manifest, files);
    }
}
