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
        ScribanTemplateAdapter.PinnedVersion,
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
