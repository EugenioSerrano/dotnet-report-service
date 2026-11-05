using ReportService.Templates;

namespace ReportService.RenderingHost;

/// <summary>
/// The templates the rendering suite prints, as the renderer reads them: the
/// manifest each declares in its own <c>template.yaml</c>, held here as the typed
/// view the composer needs. The product maps a taken record to this view; a test
/// package is a folder, so the mapping is stated once, beside the host, and held to
/// each package's own text by a test — a figure that changes in a package file and
/// not here fails that test. The shipped simple sample is registered under its own
/// id and read from its own folder, so the suite prints the package a consumer
/// copies.
/// </summary>
internal static class PackageManifests
{
    /// <summary>The package ids the rendering suite knows, in ordinal order.</summary>
    public static IReadOnlyList<string> Ids { get; } = ["integrity-grid", "integrity-table", "pilot-simple"];

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
            _ => TestPackage(),
        };
    }

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
        GroupKey: null);

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
        GroupKey: null);
}
