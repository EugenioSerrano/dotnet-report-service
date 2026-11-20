using System.Text;
using ReportService.Templates;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The three versions of one report — <c>1.0.0</c> for Scriban, <c>1.1.0</c> for Handlebars,
/// <c>1.2.0</c> for Liquid — composed from their own package folders to byte-identical HTML,
/// with the same dates, amounts and acceptance instant. The four files of each package are
/// read from the test library, so a figure that changes in a package and not in this case
/// fails here.
/// </summary>
public sealed class SameReportPackagesTests
{
    private const string Header = """{"reportTitle":"Monthly statement","customer":{"name":"Acme"},"currency":"ARS","totals":{"recordCount":3}}""";

    private static readonly string[] Records =
    [
        """{"sequence":1,"date":"2026-10-30T02:30:00Z","description":"First record","amount":1000.00}""",
        """{"sequence":2,"date":"2026-10-31T12:00:00Z","description":"Second record","amount":-0.50}""",
        """{"sequence":3,"date":"2026-11-01T23:30:00Z","description":"Third record","amount":0}""",
    ];

    public static TheoryData<string, string, string> Versions => new()
    {
        { "1.0.0", ScribanTemplateAdapter.EngineName, ScribanTemplateAdapter.EngineVersion },
        { "1.1.0", HandlebarsTemplateAdapter.EngineName, HandlebarsTemplateAdapter.EngineVersion },
        { "1.2.0", FluidTemplateAdapter.EngineName, FluidTemplateAdapter.EngineVersion },
    };

    /// <summary>The five versions and the locale each of them prints under.</summary>
    public static TheoryData<string, string, string> Locales => new()
    {
        { "1.0.0", "es-AR", "America/Argentina/Buenos_Aires" },
        { "1.1.0", "es-AR", "America/Argentina/Buenos_Aires" },
        { "1.2.0", "es-AR", "America/Argentina/Buenos_Aires" },
        { "1.3.0", "en-US", "Asia/Tokyo" },
        { "1.4.0", "en-GB", "Europe/Madrid" },
    };

    [Theory]
    [MemberData(nameof(Locales))]
    public void EachVersionDeclaresTheLocaleItPrintsUnder(string version, string culture, string timeZone)
    {
        var declaration = File.ReadAllText(Path.Combine(FolderOf(version), "template.yaml"));

        Assert.Contains("id: same-report", declaration, StringComparison.Ordinal);
        Assert.Contains($"version: \"{version}\"", declaration, StringComparison.Ordinal);
        Assert.Contains($"culture: {culture}", declaration, StringComparison.Ordinal);
        Assert.Contains($"timeZone: {timeZone}", declaration, StringComparison.Ordinal);
        Assert.Contains("item: [\"$.sequence\", \"$.description\"]", declaration, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task EachVersionDeclaresItsOwnEngineAndComposesTheSameReport(string version, string engine, string engineVersion)
    {
        var folder = FolderOf(version);
        var declaration = File.ReadAllText(Path.Combine(folder, "template.yaml"));

        Assert.Contains($"engine: {engine}", declaration, StringComparison.Ordinal);
        Assert.Contains($"engineVersion: \"{engineVersion}\"", declaration, StringComparison.Ordinal);

        var block = await Compose(version, engine);
        Assert.Contains("<h1>Monthly statement</h1>", block.Text, StringComparison.Ordinal);
        Assert.Contains("1.000,00", block.Text, StringComparison.Ordinal);
        Assert.Contains("-0,50", block.Text, StringComparison.Ordinal);
        Assert.Contains("01/11/2026 23:30", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheThreeVersionsComposeTheSameReportToByteIdenticalHtml()
    {
        var scriban = await Compose("1.0.0", ScribanTemplateAdapter.EngineName);
        var handlebars = await Compose("1.1.0", HandlebarsTemplateAdapter.EngineName);
        var liquid = await Compose("1.2.0", FluidTemplateAdapter.EngineName);

        Assert.Equal(scriban.Text, handlebars.Text);
        Assert.Equal(scriban.Text, liquid.Text);
        Assert.Equal(scriban.Html, handlebars.Html);
        Assert.Equal(scriban.Html, liquid.Html);
    }

    [Fact]
    public async Task AnEmptyReportComposesTheSameDocumentOnEveryEngine()
    {
        var scriban = await Compose("1.0.0", ScribanTemplateAdapter.EngineName, records: []);
        var handlebars = await Compose("1.1.0", HandlebarsTemplateAdapter.EngineName, records: []);
        var liquid = await Compose("1.2.0", FluidTemplateAdapter.EngineName, records: []);

        Assert.Equal(scriban.Text, handlebars.Text);
        Assert.Equal(scriban.Text, liquid.Text);
    }

    /// <summary>
    /// The source-values control: each version's composed text carries every field its own
    /// source records hold - the sequence, the date, the description and the amount, in source
    /// order - and every header field the parts print, so an adapter that presents a value
    /// differently is caught against the data, not only against its peers. The delivered
    /// family's records carry no quantity and no unit price and no delivered part prints them,
    /// so the comparison covers every field the family carries; changing the family for them
    /// would move the delivered reference.
    /// </summary>
    [Fact]
    public async Task EveryVersionPrintsEveryFieldItsSourceCarriesInSourceOrder()
    {
        (string Sequence, string Date, string Description, string Amount)[] expected =
        [
            ("1", "29/10/2026", "First record", "1.000,00"),
            ("2", "31/10/2026", "Second record", "-0,50"),
            ("3", "01/11/2026", "Third record", "0,00"),
        ];

        (string Version, string Engine)[] versions =
        [
            ("1.0.0", ScribanTemplateAdapter.EngineName),
            ("1.1.0", HandlebarsTemplateAdapter.EngineName),
            ("1.2.0", FluidTemplateAdapter.EngineName),
        ];

        foreach (var (version, engine) in versions)
        {
            var text = (await Compose(version, engine)).Text;

            Assert.Contains("<h1>Monthly statement</h1>", text, StringComparison.Ordinal);
            Assert.Contains("Acme", text, StringComparison.Ordinal);
            Assert.Contains("ARS", text, StringComparison.Ordinal);
            Assert.Contains("3</p>", text, StringComparison.Ordinal);

            var previous = -1;
            foreach (var (sequence, date, description, amount) in expected)
            {
                foreach (var field in new[] { ">" + sequence + "<", date, description, amount })
                {
                    var index = text.IndexOf(field, previous + 1, StringComparison.Ordinal);
                    Assert.True(index >= 0, $"{version} ({engine}) prints no '{field}' after its previous field");
                    previous = index;
                }
            }
        }
    }

    private static async Task<ComposedBlock> Compose(string version, string engine, string[]? records = null)
    {
        var adapter = engine switch
        {
            HandlebarsTemplateAdapter.EngineName => (ITemplateEngineAdapter)new HandlebarsTemplateAdapter(),
            FluidTemplateAdapter.EngineName => new FluidTemplateAdapter(),
            _ => new ScribanTemplateAdapter(),
        };
        var files = new FolderTemplatePackageFiles(FolderOf(version));
        var prepared = adapter.Prepare(ManifestOf(version, engine), files);
        var data = records ?? Records;
        var settings = EngineCases.BuenosAires;
        return await new BlockComposer(new ManualTimeProvider(settings.Clock)).ComposeAsync(
            new CompositionRequest(
                prepared,
                new CompositionBlock(0, data.Length, 1, IsFirst: true, IsLast: true),
                RenderContextFactory.Header(Encoding.UTF8.GetBytes(Header)),
                [.. data.Select((json, index) => new ComposedRecord(index + 1, RenderContextFactory.Header(Encoding.UTF8.GetBytes(json))))],
                new CompositionRender(settings.Culture.Name, settings.TimeZone.Id, settings.Clock),
                GroupKey: null),
            adapter,
            settings);
    }

    private static RenderManifest ManifestOf(string version, string engine)
    {
        var extension = engine switch
        {
            HandlebarsTemplateAdapter.EngineName => ".hbs",
            FluidTemplateAdapter.EngineName => ".liquid",
            _ => ".sbn",
        };
        return new RenderManifest(
            engine,
            EngineCases.EngineVersionOf(engine),
            new Dictionary<TemplatePart, string>
            {
                [TemplatePart.DocumentStart] = "html/document-start.html" + extension,
                [TemplatePart.Row] = "html/row.html" + extension,
                [TemplatePart.DocumentEnd] = "html/document-end.html" + extension,
            },
            ["html/styles.css"],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
            GroupKey: null);
    }

    private static string FolderOf(string version) =>
        Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "TemplatePackages", "same-report", version);
}
