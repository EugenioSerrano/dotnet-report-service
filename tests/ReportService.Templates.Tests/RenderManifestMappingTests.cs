using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;

namespace ReportService.Templates.Tests;

/// <summary>
/// The map from a taken version's manifest to the subset the renderer reads: where
/// the parts and the stylesheets come from, the page settings, the fields declared
/// as printed, and the cases a manifest that declares no document is refused in.
/// </summary>
public sealed class RenderManifestMappingTests
{
    [Fact]
    public void ThePartsStylesAndSettingsAreMapped()
    {
        var manifest = RenderManifestMapping.From(Record(FullManifest()));

        Assert.Equal("scriban", manifest.Engine);
        Assert.Equal("7.5.1", manifest.EngineVersion);
        Assert.Equal("html/start.sbn", manifest.PathOf(TemplatePart.DocumentStart));
        Assert.Equal("html/row.sbn", manifest.PathOf(TemplatePart.Row));
        Assert.Equal("html/end.sbn", manifest.PathOf(TemplatePart.DocumentEnd));
        Assert.False(manifest.Has(TemplatePart.GroupStart));
        Assert.Equal(["html/styles.css"], manifest.Stylesheets);
        Assert.Equal("A4", manifest.Pdf.Paper);
        Assert.Equal("portrait", manifest.Pdf.Orientation);
        Assert.Equal(15, manifest.Pdf.MarginMm.Top);
        Assert.False(manifest.DeclaresPageTotals);
        Assert.Equal(["$.reportTitle"], manifest.Pdf.Verification!.Header);
        Assert.Equal(["$.description"], manifest.Pdf.Verification.Item);
        Assert.Equal("es-AR", manifest.LocaleDefaults.Culture);
        Assert.Equal("amount", manifest.GroupKey);
    }

    [Fact]
    public void ThePdfPartsWinOverTheHtmlParts()
    {
        var manifest = FullManifest();
        manifest = manifest with
        {
            Outputs = new TemplateOutputs
            {
                Html = manifest.Outputs!.Html,
                Pdf = manifest.Outputs.Pdf! with
                {
                    Parts = new TemplateParts { DocumentStart = "pdf/start.sbn", Row = "pdf/row.sbn", DocumentEnd = "pdf/end.sbn" },
                },
            },
        };

        Assert.Equal("pdf/row.sbn", RenderManifestMapping.From(Record(manifest)).PathOf(TemplatePart.Row));
    }

    [Fact]
    public void APackageThatDeclaresNoFieldMapsToNoDeclaration()
    {
        var manifest = FullManifest();
        manifest = manifest with
        {
            Outputs = new TemplateOutputs
            {
                Html = manifest.Outputs!.Html,
                Pdf = manifest.Outputs.Pdf! with { Verification = null },
            },
        };

        Assert.Null(RenderManifestMapping.From(Record(manifest)).Pdf.Verification);
    }

    [Fact]
    public void APackageThatDeclaresPageTotalsSaysSo()
    {
        var manifest = FullManifest();
        manifest = manifest with
        {
            Outputs = new TemplateOutputs
            {
                Html = manifest.Outputs!.Html,
                Pdf = manifest.Outputs.Pdf! with { PageTotals = new TemplatePageTotals { Enabled = true } },
            },
        };

        Assert.True(RenderManifestMapping.From(Record(manifest)).DeclaresPageTotals);
    }

    [Fact]
    public void AManifestWithoutADocumentIsRefused()
    {
        Assert.Throws<TemplatePackageException>(() => RenderManifestMapping.From(Record(new TemplateManifest { Engine = TemplateEngine.Scriban })));
    }

    [Fact]
    public void AManifestWithoutAPdfDocumentIsRefused()
    {
        var manifest = FullManifest();
        manifest = manifest with { Outputs = new TemplateOutputs { Html = manifest.Outputs!.Html } };

        Assert.Throws<TemplatePackageException>(() => RenderManifestMapping.From(Record(manifest)));
    }

    [Fact]
    public void AManifestWithoutPartsIsRefused()
    {
        var manifest = FullManifest();
        manifest = manifest with
        {
            Outputs = new TemplateOutputs
            {
                Html = new TemplateHtmlOutput { Styles = ["html/styles.css"] },
                Pdf = manifest.Outputs!.Pdf,
            },
        };

        Assert.Throws<TemplatePackageException>(() => RenderManifestMapping.From(Record(manifest)));
    }

    private static TemplateManifest FullManifest() => new()
    {
        ManifestVersion = "1.0",
        Engine = TemplateEngine.Scriban,
        DeclaredEngineVersion = "7.5.1",
        Outputs = new TemplateOutputs
        {
            Html = new TemplateHtmlOutput
            {
                Parts = new TemplateParts
                {
                    DocumentStart = "html/start.sbn",
                    Row = "html/row.sbn",
                    DocumentEnd = "html/end.sbn",
                },
                Styles = ["html/styles.css"],
            },
            Pdf = new TemplatePdfOutput
            {
                Paper = "A4",
                Orientation = "portrait",
                MarginMm = new TemplateMargins { Top = 15, Right = 12, Bottom = 18, Left = 12 },
                PageTotals = new TemplatePageTotals { Enabled = false },
                Verification = new TemplatePdfVerification
                {
                    Anchors = new TemplatePdfAnchors { Header = ["$.reportTitle"], Item = ["$.description"] },
                },
            },
        },
        Partitioning = new TemplatePartitioning { GroupKey = "amount" },
        LocaleDefaults = new TemplateLocaleDefaults { Culture = "es-AR", TimeZone = "America/Argentina/Buenos_Aires" },
    };

    private static TemplateVersionRecord Record(TemplateManifest manifest) => new(
        "integrity-table",
        "1.0.0",
        "consumer",
        Shared: true,
        "1.0",
        TemplateEngine.Scriban,
        "7.5.1",
        "schema.json",
        new string('a', 40),
        new string('a', 40),
        new string('b', 64),
        10,
        4,
        default,
        manifest,
        "{}",
        DateTimeOffset.UnixEpoch);
}
