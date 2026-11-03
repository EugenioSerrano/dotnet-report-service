using ReportService.Templates;

namespace ReportService.RenderingHost;

/// <summary>
/// The two template packages the rendering suite prints, as the renderer reads
/// them: the manifest each declares in its own <c>template.yaml</c>, held here as
/// the typed view the composer needs. The product maps a taken record to this
/// view; a test package is a folder, so the mapping is stated once, beside the
/// host, and held to each package's own text by a test — a figure that changes in
/// a package file and not here fails that test.
/// </summary>
internal static class PackageManifests
{
    /// <summary>The package ids the rendering suite knows, in ordinal order.</summary>
    public static IReadOnlyList<string> Ids { get; } = ["integrity-grid", "integrity-table"];

    /// <summary>The declared manifest of the package <paramref name="id"/>.</summary>
    public static RenderManifest Of(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Ids.Contains(id, StringComparer.Ordinal))
        {
            throw new ArgumentException($"The rendering suite knows no package '{id}'.", nameof(id));
        }

        return new RenderManifest(
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
    }
}
