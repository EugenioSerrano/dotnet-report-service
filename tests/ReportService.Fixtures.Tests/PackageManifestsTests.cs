using ReportService.RenderingHost;
using ReportService.Templates;
using ReportService.Testing;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The manifest the rendering host declares for each test package, held to the
/// package's own files: the host composes a package from this declaration, so a
/// figure the package changes without the declaration fails here instead of
/// printing a page nobody described.
/// </summary>
public sealed class PackageManifestsTests
{
    private static string TemplatesDirectory => Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates");

    /// <summary>The host knows exactly the packages the test library holds.</summary>
    [Fact]
    public void TheHostKnowsEveryTestPackageAndNoOther()
    {
        var folders = Directory.GetDirectories(TemplatesDirectory)
            .Select(folder => Path.GetFileName(folder))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(folders, PackageManifests.Ids.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Every declared part and stylesheet is a file of the package.</summary>
    /// <param name="id">The package.</param>
    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void EveryDeclaredFileIsInThePackage(string id)
    {
        var manifest = PackageManifests.Of(id);
        var folder = Path.Combine(TemplatesDirectory, id);

        foreach (var part in manifest.Parts.Values)
        {
            Assert.True(File.Exists(Path.Combine(folder, part)), $"{id} declares the part {part}, which the package does not hold.");
        }

        foreach (var stylesheet in manifest.Stylesheets)
        {
            Assert.True(File.Exists(Path.Combine(folder, stylesheet)), $"{id} declares the stylesheet {stylesheet}, which the package does not hold.");
        }
    }

    /// <summary>The declared engine, page and anchors are the figures the package states.</summary>
    /// <param name="id">The package.</param>
    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void TheDeclarationMatchesThePackageManifest(string id)
    {
        var manifest = PackageManifests.Of(id);
        var text = File.ReadAllText(Path.Combine(TemplatesDirectory, id, "template.yaml"));

        Assert.Contains($"id: {id}", text, StringComparison.Ordinal);
        Assert.Contains($"engine: {manifest.Engine}", text, StringComparison.Ordinal);
        Assert.Contains($"engineVersion: \"{manifest.EngineVersion}\"", text, StringComparison.Ordinal);
        Assert.Contains($"paper: {manifest.Pdf.Paper}", text, StringComparison.Ordinal);
        Assert.Contains($"orientation: {manifest.Pdf.Orientation}", text, StringComparison.Ordinal);
        Assert.Contains(
            $"marginMm: {{ top: {manifest.Pdf.MarginMm.Top}, right: {manifest.Pdf.MarginMm.Right}, bottom: {manifest.Pdf.MarginMm.Bottom}, left: {manifest.Pdf.MarginMm.Left} }}",
            text,
            StringComparison.Ordinal);
        Assert.Equal(manifest.Pdf.PageTotalsEnabled, text.Contains("enabled: true", StringComparison.Ordinal));
        Assert.NotNull(manifest.Pdf.Verification);

        foreach (var anchor in manifest.Pdf.Verification!.Header.Concat(manifest.Pdf.Verification.Item))
        {
            Assert.Contains($"\"{anchor}\"", text, StringComparison.Ordinal);
        }
    }

    /// <summary>The page the package declares is the page its stylesheet gives the sheet.</summary>
    [Fact]
    public void TheStylesheetsDeclareThePageTheManifestGives()
    {
        foreach (var id in PackageManifests.Ids)
        {
            var manifest = PackageManifests.Of(id);
            var stylesheet = File.ReadAllText(Path.Combine(TemplatesDirectory, id, "html", "styles.css"));
            var margins = manifest.Pdf.MarginMm;

            Assert.Contains($"size: {manifest.Pdf.Paper} {manifest.Pdf.Orientation};", stylesheet, StringComparison.Ordinal);
            Assert.Contains($"margin: {margins.Top}mm {margins.Right}mm {margins.Bottom}mm {margins.Left}mm;", stylesheet, StringComparison.Ordinal);
        }
    }

    /// <summary>The composer prepares every declared package with the pinned engine.</summary>
    [Fact]
    public void EveryPackageIsPreparedByThePinnedEngine()
    {
        foreach (var id in PackageManifests.Ids)
        {
            var manifest = PackageManifests.Of(id);
            var files = new FolderTemplatePackageFiles(Path.Combine(TemplatesDirectory, id));

            var prepared = new ScribanTemplateAdapter().Prepare(manifest, files);

            Assert.True(prepared.Has(TemplatePart.DocumentStart));
            Assert.True(prepared.Has(TemplatePart.Row));
            Assert.True(prepared.Has(TemplatePart.DocumentEnd));
        }
    }
}
