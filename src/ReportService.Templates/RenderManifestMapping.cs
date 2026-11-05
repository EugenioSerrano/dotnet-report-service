using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;

namespace ReportService.Templates;

/// <summary>
/// Maps the manifest a taken version was recorded with onto the subset the
/// renderer reads. Nothing here is read from the template source: the record the
/// take fixed is the only input, so a render can never see a version other than
/// the one its job pinned.
/// </summary>
public static class RenderManifestMapping
{
    /// <summary>Maps <paramref name="record"/>'s manifest.</summary>
    /// <param name="record">The taken version.</param>
    /// <exception cref="TemplatePackageException">The manifest lacks what a render needs.</exception>
    public static RenderManifest From(TemplateVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return From(record.Manifest, record.Engine, record.EngineVersion);
    }

    /// <summary>Maps a stored manifest.</summary>
    /// <param name="manifest">The manifest the version was recorded with.</param>
    /// <param name="engine">The engine the record fixed.</param>
    /// <param name="engineVersion">The engine version the record fixed.</param>
    /// <exception cref="TemplatePackageException">The manifest lacks what a render needs.</exception>
    public static RenderManifest From(TemplateManifest manifest, TemplateEngine engine, string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);

        var outputs = manifest.Outputs
            ?? throw new TemplatePackageException("The package's manifest declares no output.");
        var parts = outputs.Pdf?.Parts ?? outputs.Html?.Parts
            ?? throw new TemplatePackageException("The package's manifest declares no parts.");
        var pdf = outputs.Pdf
            ?? throw new TemplatePackageException("The package's manifest declares no PDF output.");

        var declared = new Dictionary<TemplatePart, string>(3);
        Add(declared, TemplatePart.DocumentStart, parts.DocumentStart);
        Add(declared, TemplatePart.GroupStart, parts.GroupStart);
        Add(declared, TemplatePart.Row, parts.Row);
        Add(declared, TemplatePart.GroupEnd, parts.GroupEnd);
        Add(declared, TemplatePart.DocumentEnd, parts.DocumentEnd);

        return new RenderManifest(
            TemplateEngineNames.ToManifestName(engine),
            engineVersion,
            declared,
            outputs.Html?.Styles ?? [],
            PdfOf(pdf),
            new LocaleDefaults(manifest.LocaleDefaults?.Culture, manifest.LocaleDefaults?.TimeZone),
            manifest.Partitioning?.GroupKey);
    }

    private static PdfSettings PdfOf(TemplatePdfOutput pdf) => new(
        pdf.Paper ?? "A4",
        pdf.Orientation ?? "portrait",
        new PageMarginsMm(
            (double)(pdf.MarginMm?.Top ?? 0m),
            (double)(pdf.MarginMm?.Right ?? 0m),
            (double)(pdf.MarginMm?.Bottom ?? 0m),
            (double)(pdf.MarginMm?.Left ?? 0m)),
        pdf.PageTotals?.Enabled ?? false,
        AnchorsOf(pdf.Verification?.Anchors));

    /// <summary>
    /// The declared fields, or <see langword="null"/> when the package declares none:
    /// a missing declaration is never read as "no fields", because a package that
    /// prints nothing as it is cannot be bound to its own data.
    /// </summary>
    private static VerificationAnchors? AnchorsOf(TemplatePdfAnchors? anchors)
    {
        if (anchors is null)
        {
            return null;
        }

        return new VerificationAnchors(anchors.Header ?? [], anchors.Item ?? []);
    }

    private static void Add(Dictionary<TemplatePart, string> parts, TemplatePart part, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            parts[part] = path;
        }
    }
}
