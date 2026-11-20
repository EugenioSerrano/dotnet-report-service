using ReportService.Testing;
using ReportService.Templates;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The two template packages the rendering suite prints: their declared facts,
/// their files, and that the pinned engine parses every part of them.
/// </summary>
public sealed class TestTemplatePackageTests
{
    private static readonly string[] Anchors =
    [
        "$.reportTitle",
        "$.customer.name",
        "$.currency",
        "$.totals.recordCount",
        "$.sequence",
        "$.date",
        "$.description",
        "$.amount",
    ];

    private static string TemplatesDirectory => Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates");

    private static string Folder(string id) => Path.Combine(TemplatesDirectory, id);

    private static string ManifestText(string id) => File.ReadAllText(Path.Combine(Folder(id), "template.yaml"));

    private static RenderManifest Manifest() => new(
        ScribanTemplateAdapter.EngineName,
        ScribanTemplateAdapter.EngineVersion,
        new Dictionary<TemplatePart, string>
        {
            [TemplatePart.DocumentStart] = "html/document-start.html.sbn",
            [TemplatePart.Row] = "html/row.html.sbn",
            [TemplatePart.DocumentEnd] = "html/document-end.html.sbn",
        },
        ["html/styles.css"],
        new PdfSettings(
            "A4",
            "portrait",
            new PageMarginsMm(Top: 15, Right: 12, Bottom: 18, Left: 12),
            PageTotalsEnabled: false,
            new VerificationAnchors(
                ["$.reportTitle", "$.customer.name", "$.currency", "$.totals.recordCount"],
                ["$.sequence", "$.date", "$.description", "$.amount"])),
        new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
        GroupKey: null);

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void ThePackageHoldsTheFilesItsManifestNames(string id)
    {
        var folder = Folder(id);

        Assert.True(File.Exists(Path.Combine(folder, "template.yaml")));
        Assert.True(File.Exists(Path.Combine(folder, "html", "document-start.html.sbn")));
        Assert.True(File.Exists(Path.Combine(folder, "html", "row.html.sbn")));
        Assert.True(File.Exists(Path.Combine(folder, "html", "document-end.html.sbn")));
        Assert.True(File.Exists(Path.Combine(folder, "html", "styles.css")));
    }

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void ThePackageDeclaresThePinnedEngineAndTheNativePage(string id)
    {
        var manifest = ManifestText(id);

        Assert.Contains($"id: {id}", manifest, StringComparison.Ordinal);
        Assert.Contains("engine: scriban", manifest, StringComparison.Ordinal);
        Assert.Contains("engineVersion: \"7.5.1\"", manifest, StringComparison.Ordinal);
        Assert.Contains("profile: header-items-v1", manifest, StringComparison.Ordinal);
        Assert.Contains("paper: A4", manifest, StringComparison.Ordinal);
        Assert.Contains("orientation: portrait", manifest, StringComparison.Ordinal);
        Assert.Contains("marginMm: { top: 15, right: 12, bottom: 18, left: 12 }", manifest, StringComparison.Ordinal);
        Assert.Contains("enabled: false", manifest, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void ThePackageDeclaresEveryFieldItPrintsAsItIs(string id)
    {
        var manifest = ManifestText(id);

        foreach (var anchor in Anchors)
        {
            Assert.Contains($"\"{anchor}\"", manifest, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void ThePartsPrintTheFieldsTheyDeclare(string id)
    {
        var start = File.ReadAllText(Path.Combine(Folder(id), "html", "document-start.html.sbn"));
        var row = File.ReadAllText(Path.Combine(Folder(id), "html", "row.html.sbn"));

        foreach (var path in new[] { "header.reportTitle", "header.customer.name", "header.currency", "header.totals.recordCount" })
        {
            Assert.Contains(path, start, StringComparison.Ordinal);
        }

        foreach (var path in new[] { "item.sequence", "item.date", "item.description", "item.amount" })
        {
            Assert.Contains(path, row, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void ThePinnedEngineParsesEveryPart(string id)
    {
        var prepared = new ScribanTemplateAdapter().Prepare(Manifest(), new FolderTemplatePackageFiles(Folder(id)));

        Assert.True(prepared.Has(TemplatePart.DocumentStart));
        Assert.True(prepared.Has(TemplatePart.Row));
        Assert.True(prepared.Has(TemplatePart.DocumentEnd));
        Assert.False(prepared.Has(TemplatePart.GroupStart));
        Assert.False(prepared.Has(TemplatePart.GroupEnd));
    }

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void ThePackageFetchesItsStylesheetFromItsOwnOrigin(string id)
    {
        var start = File.ReadAllText(Path.Combine(Folder(id), "html", "document-start.html.sbn"));

        Assert.Contains("href=\"https://report.invalid/html/styles.css\"", start, StringComparison.Ordinal);
    }
}

/// <summary>
/// The takeable fixture family added for the sample render: three versions of one
/// report that compose the same bytes, each carrying its owner and its own
/// <c>sample.json</c>, held to its own files so a package that changes without its
/// declaration fails here. The delivered <c>same-report</c> family is not this
/// family and is not edited.
/// </summary>
public sealed class SampleGatePackageTests
{
    private static readonly string[] Versions = ["1.0.0", "1.1.0", "1.2.0"];

    private static readonly (string Version, string Engine, string EngineVersion, string Extension)[] Expected =
    [
        ("1.0.0", "scriban", "7.5.1", "sbn"),
        ("1.1.0", "handlebars", "2.4.3", "hbs"),
        ("1.2.0", "liquid", "2.40.0", "liquid"),
    ];

    private static string Family => Path.Combine(
        RepositoryPaths.Root, "tests", "ReportService.Testing", "TemplatePackages", "sample-gate");

    [Fact]
    public void TheFamilyHoldsTheThreeVersionsTheTakeSeeds()
    {
        Assert.Equal(
            Versions,
            Directory.GetDirectories(Family).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("1.0.0", "scriban", "7.5.1", "sbn")]
    [InlineData("1.1.0", "handlebars", "2.4.3", "hbs")]
    [InlineData("1.2.0", "liquid", "2.40.0", "liquid")]
    public void EachVersionDeclaresItsOwnEngineAndItsOwnFiles(string version, string engine, string engineVersion, string extension)
    {
        var folder = Path.Combine(Family, version);
        var declaration = File.ReadAllText(Path.Combine(folder, "template.yaml"));

        Assert.Contains("id: sample-gate", declaration, StringComparison.Ordinal);
        Assert.Contains($"version: \"{version}\"", declaration, StringComparison.Ordinal);
        Assert.Contains("owner: billing-system", declaration, StringComparison.Ordinal);
        Assert.Contains("shared: false", declaration, StringComparison.Ordinal);
        Assert.Contains($"engine: {engine}", declaration, StringComparison.Ordinal);
        Assert.Contains($"engineVersion: \"{engineVersion}\"", declaration, StringComparison.Ordinal);
        Assert.Contains("culture: es-AR", declaration, StringComparison.Ordinal);
        Assert.Contains("timeZone: America/Argentina/Buenos_Aires", declaration, StringComparison.Ordinal);
        Assert.Contains("item: [\"$.sequence\", \"$.description\"]", declaration, StringComparison.Ordinal);

        foreach (var part in new[] { "document-start", "row", "document-end" })
        {
            Assert.True(
                File.Exists(Path.Combine(folder, "html", $"{part}.html.{extension}")),
                $"{version} declares and must hold html/{part}.{extension}");
            Assert.Contains($"html/{part}.html.{extension}", declaration, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(folder, "schema.json")));
        Assert.True(File.Exists(Path.Combine(folder, "sample.json")));
        Assert.True(File.Exists(Path.Combine(folder, "html", "styles.css")));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    public void TheSampleCarriesTheThreeRecordsTheReportPrints(string version)
    {
        var sample = File.ReadAllText(Path.Combine(Family, version, "sample.json"));

        using var document = System.Text.Json.JsonDocument.Parse(sample);
        var items = document.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(3, document.RootElement.GetProperty("header").GetProperty("totals").GetProperty("recordCount").GetInt32());
        foreach (var item in items.EnumerateArray())
        {
            foreach (var field in new[] { "sequence", "date", "description", "quantity", "unitPrice", "amount" })
            {
                Assert.True(item.TryGetProperty(field, out _), $"{version}'s sample holds no {field}");
            }
        }
    }

    [Fact]
    public void TheDeliveredSameReportFamilyIsUntouched()
    {
        var sameReport = Path.Combine(
            RepositoryPaths.Root, "tests", "ReportService.Testing", "TemplatePackages", "same-report");

        foreach (var version in Directory.GetDirectories(sameReport))
        {
            Assert.False(File.Exists(Path.Combine(version, "sample.json")), $"{version} gained a sample");
            Assert.DoesNotContain(
                "owner:",
                File.ReadAllText(Path.Combine(version, "template.yaml")),
                StringComparison.Ordinal);
        }
    }
}