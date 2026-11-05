using ReportService.RenderingHost;
using ReportService.Templates;
using ReportService.Testing;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The manifest the rendering host declares for each package, held to the package's
/// own files: the host composes a package from this declaration, so a figure the
/// package changes without the declaration fails here instead of printing a page
/// nobody described. The host knows two roots: the test library's packages, and the
/// one shipped sample the rendering suite prints as a consumer would copy it.
/// </summary>
public sealed class PackageManifestsTests
{
    private const string SimpleSample = "pilot-simple";

    private static string TemplatesDirectory => Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates");

    private static string SamplesDirectory => Path.Combine(RepositoryPaths.Root, "samples", "templates");

    /// <summary>
    /// The host knows exactly the test library's packages together with the one
    /// shipped sample the rendering suite prints: a folder added to either root
    /// without a registration fails here, and an id registered with a folder in
    /// neither root fails here too. The two samples that declare page totals are not
    /// registered: the product refuses their accounting path, no case prints them,
    /// and their printability belongs to the change that delivers that path.
    /// </summary>
    [Fact]
    public void TheHostKnowsTheTestLibraryAndTheShippedSampleItPrints()
    {
        var expected = Directory.GetDirectories(TemplatesDirectory)
            .Select(folder => Path.GetFileName(folder))
            .Append(SimpleSample)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, PackageManifests.Ids.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>The folder that holds the package <paramref name="id"/>, under its own root.</summary>
    /// <param name="id">The package.</param>
    private static string PackageFolder(string id)
    {
        var tested = Path.Combine(TemplatesDirectory, id);
        if (Directory.Exists(tested))
        {
            return tested;
        }

        var shipped = Path.Combine(SamplesDirectory, id);
        return Directory.Exists(shipped)
            ? shipped
            : throw new InvalidOperationException($"The package '{id}' is under neither {TemplatesDirectory} nor {SamplesDirectory}.");
    }

    /// <summary>Every declared part and stylesheet is a file of the package.</summary>
    /// <param name="id">The package.</param>
    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    [InlineData(SimpleSample)]
    public void EveryDeclaredFileIsInThePackage(string id)
    {
        var manifest = PackageManifests.Of(id);
        var folder = PackageFolder(id);

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
    [InlineData(SimpleSample)]
    public void TheDeclarationMatchesThePackageManifest(string id)
    {
        var manifest = PackageManifests.Of(id);
        var text = File.ReadAllText(Path.Combine(PackageFolder(id), "template.yaml"));

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
            var stylesheet = File.ReadAllText(Path.Combine(PackageFolder(id), "html", "styles.css"));
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
            var files = new FolderTemplatePackageFiles(PackageFolder(id));

            var prepared = new ScribanTemplateAdapter().Prepare(manifest, files);

            Assert.True(prepared.Has(TemplatePart.DocumentStart));
            Assert.True(prepared.Has(TemplatePart.Row));
            Assert.True(prepared.Has(TemplatePart.DocumentEnd));
        }
    }

    /// <summary>
    /// Every shipped sample names the stylesheet its package holds: the document is
    /// served at the package root, so the part links the package-relative path the
    /// manifest declares, the file exists, and no part names the file relative to its
    /// own folder.
    /// </summary>
    /// <param name="id">The shipped sample.</param>
    [Theory]
    [InlineData(SimpleSample)]
    [InlineData("pilot-grouped")]
    [InlineData("pilot-volume")]
    public void EveryShippedSampleNamesTheStylesheetItsPackageHolds(string id)
    {
        const string stylesheet = "html/styles.css";
        var folder = Path.Combine(SamplesDirectory, id);

        var start = File.ReadAllText(Path.Combine(folder, "html", "document-start.html.sbn"));
        Assert.Contains($"href=\"{stylesheet}\"", start, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, stylesheet)), $"{id} links {stylesheet}, which the package does not hold.");

        var manifest = File.ReadAllText(Path.Combine(folder, "template.yaml"));
        Assert.Contains($"styles: [{stylesheet}]", manifest, StringComparison.Ordinal);

        foreach (var part in Directory.EnumerateFiles(Path.Combine(folder, "html"), "*.sbn"))
        {
            Assert.DoesNotContain("href=\"styles.css\"", File.ReadAllText(part), StringComparison.Ordinal);
        }
    }
}
