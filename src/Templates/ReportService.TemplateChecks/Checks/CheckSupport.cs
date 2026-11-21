using System.Text.Json;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Checks;

/// <summary>What the checks share: the budget's refusal, the dialet of an engine, the parts a manifest declares.</summary>
internal static class CheckSupport
{
    /// <summary>The budget's own refusal.</summary>
    internal static TemplateCheckResult BudgetRefusal() =>
        TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            PackagePath.ManifestFileName,
            "the template checks did not finish within 30 s.",
            "Reduce the package's size or complexity.");

    /// <summary>The scan dialect of a declared engine.</summary>
    internal static Scanning.TemplateDialect Dialect(TemplateEngine engine) => engine switch
    {
        TemplateEngine.Scriban => Scanning.TemplateDialect.Scriban,
        TemplateEngine.Handlebars => Scanning.TemplateDialect.Handlebars,
        _ => Scanning.TemplateDialect.Liquid,
    };

    /// <summary>The part extension of a declared engine.</summary>
    internal static string Extension(TemplateEngine engine) => engine switch
    {
        TemplateEngine.Scriban => ReportService.Templates.PartExtensions.Scriban,
        TemplateEngine.Handlebars => ReportService.Templates.PartExtensions.Handlebars,
        _ => ReportService.Templates.PartExtensions.Liquid,
    };

    /// <summary>The parts the manifest declares, by the manifest's own names.</summary>
    internal static IReadOnlyDictionary<string, string> Parts(TemplateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(parts, "outputs.html.parts.documentStart", manifest.Outputs?.Html?.Parts?.DocumentStart);
        Add(parts, "outputs.html.parts.groupStart", manifest.Outputs?.Html?.Parts?.GroupStart);
        Add(parts, "outputs.html.parts.row", manifest.Outputs?.Html?.Parts?.Row);
        Add(parts, "outputs.html.parts.groupEnd", manifest.Outputs?.Html?.Parts?.GroupEnd);
        Add(parts, "outputs.html.parts.documentEnd", manifest.Outputs?.Html?.Parts?.DocumentEnd);
        Add(parts, "outputs.pdf.parts.documentStart", manifest.Outputs?.Pdf?.Parts?.DocumentStart);
        Add(parts, "outputs.pdf.parts.groupStart", manifest.Outputs?.Pdf?.Parts?.GroupStart);
        Add(parts, "outputs.pdf.parts.row", manifest.Outputs?.Pdf?.Parts?.Row);
        Add(parts, "outputs.pdf.parts.groupEnd", manifest.Outputs?.Pdf?.Parts?.GroupEnd);
        Add(parts, "outputs.pdf.parts.documentEnd", manifest.Outputs?.Pdf?.Parts?.DocumentEnd);
        return parts;
    }

    /// <summary>The distinct package paths the parts of a document point at.</summary>
    internal static IReadOnlyList<string> PartPaths(TemplateManifest manifest) =>
        [.. Parts(manifest).Values.Distinct(StringComparer.Ordinal)];

    /// <summary>Every stylesheet the manifest declares.</summary>
    internal static IReadOnlyList<string> Styles(TemplateManifest manifest) =>
        manifest.Outputs?.Html?.Styles ?? [];

    /// <summary>The schema document's root element, or null when it does not parse.</summary>
    internal static JsonDocument? ParseSchema(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Add(Dictionary<string, string> parts, string field, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            parts[field] = path;
        }
    }
}
