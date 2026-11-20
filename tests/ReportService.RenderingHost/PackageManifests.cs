using System.Text.Json;
using ReportService.Templates;

namespace ReportService.RenderingHost;

/// <summary>
/// The templates the rendering suite prints, as the renderer reads them: the
/// manifest each declares in its own <c>template.yaml</c>, held here as the typed
/// view the composer needs, with the <c>outputs.pdf</c> element exactly as the file
/// stores it. The product maps a taken record to this view; a test package is a
/// folder, so the mapping is stated once, beside the host, and held to each package's
/// own text by a test — a figure that changes in a package file and not here fails
/// that test. The shipped simple sample is registered under its own id and read from
/// its own folder, so the suite prints the package a consumer copies.
/// </summary>
internal static class PackageManifests
{
    /// <summary>The package id of the page frame's canonical declaration, A4 portrait.</summary>
    public const string PageFrame = "page-frame";

    /// <summary>The prefix a version of the same report is printed under, so the flat package folder names its engine.</summary>
    public const string SameReportPrefix = "same-report-";

    /// <summary>The prefix a takeable sample-gate version is printed under, beside the delivered family.</summary>
    public const string SampleGatePrefix = "sample-gate-";

    /// <summary>The five versions of the same report: three engines in the base locale and two other locales.</summary>
    public static IReadOnlyList<string> SameReportVersions { get; } = ["1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0"];

    /// <summary>The three takeable versions of the sample-gate family, one engine each.</summary>
    public static IReadOnlyList<string> SampleGateVersions { get; } = ["1.0.0", "1.1.0", "1.2.0"];

    /// <summary>The three takeable versions, under the flat names the suite mounts them as.</summary>
    public static IReadOnlyList<string> SampleGatePackages { get; } =
        [.. SampleGateVersions.Select(version => SampleGatePrefix + version)];

    /// <summary>The three versions that print the report in three engines, under the base locale.</summary>
    public static IReadOnlyList<string> SameReportEngines { get; } = [SameReportPrefix + "1.0.0", SameReportPrefix + "1.1.0", SameReportPrefix + "1.2.0"];

    /// <summary>The base locale and the two locale variants: the same report, presented under three settings.</summary>
    public static IReadOnlyList<string> SameReportLocales { get; } = [SameReportPrefix + "1.0.0", SameReportPrefix + "1.3.0", SameReportPrefix + "1.4.0"];

    /// <summary>The package ids the rendering suite knows, in ordinal order.</summary>
    public static IReadOnlyList<string> Ids { get; } =
    [
        "integrity-grid",
        "integrity-table",
        PageFrame,
        PageFrame + "-a4-landscape",
        PageFrame + "-letter-landscape",
        PageFrame + "-letter-portrait",
        "pilot-simple",
        .. SameReportVersions.Select(version => SameReportPrefix + version),
        .. SampleGatePackages,
    ];

    /// <summary>The four geometries the page-frame package is printed on.</summary>
    public static IReadOnlyList<string> PageFrameVariants { get; } =
    [
        PageFrame,
        PageFrame + "-a4-landscape",
        PageFrame + "-letter-portrait",
        PageFrame + "-letter-landscape",
    ];

    /// <summary>The declared manifest of the package <paramref name="id"/>.</summary>
    public static RenderManifest Of(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Ids.Contains(id, StringComparer.Ordinal))
        {
            throw new ArgumentException($"The rendering suite knows no package '{id}'.", nameof(id));
        }

        return id switch
        {
            "pilot-simple" => ShippedSimpleSample(),
            PageFrame => PageFrameManifest(A4Portrait),
            PageFrame + "-a4-landscape" => PageFrameManifest(A4Landscape),
            PageFrame + "-letter-portrait" => PageFrameManifest(LetterPortrait),
            PageFrame + "-letter-landscape" => PageFrameManifest(LetterLandscape),
            _ when id.StartsWith(SameReportPrefix, StringComparison.Ordinal) => SameReport(id[SameReportPrefix.Length..]),
            _ when id.StartsWith(SampleGatePrefix, StringComparison.Ordinal) => SameReport(id[SampleGatePrefix.Length..]),
            _ => TestPackage(),
        };
    }

    /// <summary>The variant whose folder is <paramref name="id"/>'s own, or <see langword="null"/> when it shares another folder.</summary>
    /// <param name="id">The package id.</param>
    public static string? FolderOf(string id) => id.StartsWith(PageFrame, StringComparison.Ordinal) ? PageFrame : id;

    /// <summary>The <c>outputs.pdf</c> element a variant declares.</summary>
    /// <param name="id">The package id.</param>
    public static JsonElement PdfOutputOf(string id) => Of(id).PdfOutput;

    // The two test packages of the fixture library declare the same manifest.
    private static RenderManifest TestPackage() => new(
        "scriban",
        "7.5.1",
        new Dictionary<TemplatePart, string>
        {
            [TemplatePart.DocumentStart] = "html/document-start.html.sbn",
            [TemplatePart.Row] = "html/row.html.sbn",
            [TemplatePart.DocumentEnd] = "html/document-end.html.sbn",
        },
        ["html/styles.css"],
        new PdfSettings(
            Paper: "A4",
            Orientation: "portrait",
            new PageMarginsMm(Top: 15, Right: 12, Bottom: 18, Left: 12),
            PageTotalsEnabled: false,
            new VerificationAnchors(
                ["$.reportTitle", "$.customer.name", "$.currency", "$.totals.recordCount"],
                ["$.sequence", "$.date", "$.description", "$.amount"])),
        new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
        GroupKey: null,
        PdfOutput: Pdf(A4Portrait));

    // One version of the same report: the engine it declares, its three parts, its
    // stylesheet, and the locale the version presents under. The three engines' versions
    // share one declaration here; the two locale variants differ from 1.0.0 in the two
    // localeDefaults lines alone.
    private static RenderManifest SameReport(string version)
    {
        var (engine, extension) = version switch
        {
            "1.1.0" => (HandlebarsTemplateAdapter.EngineName, "hbs"),
            "1.2.0" => (FluidTemplateAdapter.EngineName, "liquid"),
            _ => (ScribanTemplateAdapter.EngineName, "sbn"),
        };
        var versionOf = engine switch
        {
            HandlebarsTemplateAdapter.EngineName => HandlebarsTemplateAdapter.EngineVersion,
            FluidTemplateAdapter.EngineName => FluidTemplateAdapter.EngineVersion,
            _ => ScribanTemplateAdapter.EngineVersion,
        };
        var locale = version switch
        {
            "1.3.0" => new LocaleDefaults("en-US", "Asia/Tokyo"),
            "1.4.0" => new LocaleDefaults("en-GB", "Europe/Madrid"),
            _ => new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
        };
        return new RenderManifest(
            engine,
            versionOf,
            new Dictionary<TemplatePart, string>
            {
                [TemplatePart.DocumentStart] = "html/document-start.html." + extension,
                [TemplatePart.Row] = "html/row.html." + extension,
                [TemplatePart.DocumentEnd] = "html/document-end.html." + extension,
            },
            ["html/styles.css"],
            new PdfSettings(
                Paper: "A4",
                Orientation: "portrait",
                new PageMarginsMm(Top: 15, Right: 12, Bottom: 18, Left: 12),
                PageTotalsEnabled: false,
                new VerificationAnchors(
                    ["$.reportTitle", "$.customer.name", "$.currency", "$.totals.recordCount"],
                    ["$.sequence", "$.description"])),
            locale,
            GroupKey: null,
            PdfOutput: Pdf(A4Portrait));
    }

    // The page frame's own declaration: four geometries that differ only in outputs.pdf.
    private static RenderManifest PageFrameManifest(string pdfOutput)
    {
        var declared = OfPdf(pdfOutput);
        return new RenderManifest(
            "scriban",
            "7.5.1",
            new Dictionary<TemplatePart, string>
            {
                [TemplatePart.DocumentStart] = "html/document-start.html.sbn",
                [TemplatePart.Row] = "html/row.html.sbn",
                [TemplatePart.DocumentEnd] = "html/document-end.html.sbn",
            },
            ["html/page-frame.css"],
            declared,
            new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
            GroupKey: null,
            PdfOutput: Pdf(pdfOutput));
    }

    // The shipped sample's own declaration, read from its template.yaml and its
    // stylesheet: one block, no page totals, no partitioning, and every field its
    // parts print as they are.
    private static RenderManifest ShippedSimpleSample() => new(
        "scriban",
        "7.5.1",
        new Dictionary<TemplatePart, string>
        {
            [TemplatePart.DocumentStart] = "html/document-start.html.sbn",
            [TemplatePart.Row] = "html/row.html.sbn",
            [TemplatePart.DocumentEnd] = "html/document-end.html.sbn",
        },
        ["html/styles.css"],
        new PdfSettings(
            Paper: "A4",
            Orientation: "portrait",
            new PageMarginsMm(Top: 15, Right: 12, Bottom: 18, Left: 12),
            PageTotalsEnabled: false,
            new VerificationAnchors(
                ["$.reportTitle", "$.customer.name", "$.customer.id", "$.currency", "$.generatedAt", "$.totals.recordCount", "$.totals.amount"],
                ["$.sequence", "$.description", "$.quantity", "$.unitPrice", "$.amount"])),
        new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
        GroupKey: null,
        PdfOutput: Pdf(A4Portrait));

    private const string A4Portrait = """{"paper":"A4","orientation":"portrait","marginMm":{"top":15,"right":12,"bottom":18,"left":12}}""";

    private const string A4Landscape = """{"paper":"A4","orientation":"landscape","marginMm":{"top":20,"right":15,"bottom":20,"left":15}}""";

    private const string LetterPortrait = """{"paper":"Letter","orientation":"portrait","marginMm":{"top":25.4,"right":19.05,"bottom":25.4,"left":19.05}}""";

    private const string LetterLandscape = """{"paper":"Letter","orientation":"landscape","marginMm":{"top":19.05,"right":25.4,"bottom":19.05,"left":25.4}}""";

    /// <summary>The <c>outputs.pdf</c> element a declaration holds.</summary>
    /// <param name="text">The element as JSON.</param>
    public static JsonElement Pdf(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // The typed view of the element the declaration holds, read through the product's
    // own rule so the host never states a page the product would refuse.
    private static PdfSettings OfPdf(string pdfOutput)
    {
        var settings = ReportService.TemplatePackages.Manifest.PdfPageSettingsRules.Resolve(Pdf(pdfOutput)).Settings
            ?? throw new InvalidOperationException($"The host's declaration is not a usable page: {pdfOutput}");
        return new PdfSettings(
            settings.PaperName,
            settings.OrientationName,
            new PageMarginsMm(
                (double)settings.MarginTopMm,
                (double)settings.MarginRightMm,
                (double)settings.MarginBottomMm,
                (double)settings.MarginLeftMm),
            PageTotalsEnabled: false,
            new VerificationAnchors(
                ["$.reportTitle", "$.customer.name", "$.customer.id", "$.currency"],
                ["$.sequence", "$.date", "$.description", "$.amount"]));
    }
}
