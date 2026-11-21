using System.Text.Json;

namespace ReportService.TemplatePackages.Manifest;

/// <summary>
/// The outcome of reading a manifest's PDF page settings: the page that every page
/// of the printed document must have, or the manifest path of the first member that
/// makes the declaration unusable. A refusal names the field and never its value.
/// </summary>
public sealed record PdfPageSettingsResult
{
    private PdfPageSettingsResult(PdfPageSettings? settings, string? unusableField)
    {
        Settings = settings;
        UnusableField = unusableField;
    }

    /// <summary>The resolved page, or <see langword="null"/> when the declaration is unusable.</summary>
    public PdfPageSettings? Settings { get; }

    /// <summary>The manifest path of the first offending member, or <see langword="null"/> when it resolved.</summary>
    public string? UnusableField { get; }

    /// <summary>Whether the declaration names a page the service prints.</summary>
    public bool Resolved => Settings is not null;

    /// <summary>The resolved page.</summary>
    /// <param name="settings">The page the declaration names.</param>
    public static PdfPageSettingsResult Of(PdfPageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PdfPageSettingsResult(settings, null);
    }

    /// <summary>The refusal of a declaration whose first offending member is <paramref name="fieldPath"/>.</summary>
    /// <param name="fieldPath">The manifest path of the offending member, never its value.</param>
    public static PdfPageSettingsResult Unusable(string fieldPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPath);
        return new PdfPageSettingsResult(null, fieldPath);
    }
}

/// <summary>
/// The one definition of the page settings a PDF template declares, read from the
/// manifest's stored JSON: the paper (A4 or Letter, compared without ASCII case),
/// the orientation (portrait or landscape, compared without ASCII case), the four
/// margins (every one present, a JSON number, not negative), and a printable area of
/// at least fifty millimetres on each side. A value of the wrong JSON kind is
/// reported with its path - the stored document keeps its kinds, so "15" is a string
/// and is never read as a number. The first offending member is reported in the
/// order this type checks them, and its value is never part of the report.
/// </summary>
public static class PdfPageSettingsRules
{
    /// <summary>The smallest printable area each side of a page may keep, in millimetres.</summary>
    public const decimal MinimumPrintableMm = 50m;

    /// <summary>The manifest path of the paper.</summary>
    public const string PaperField = "outputs.pdf.paper";

    /// <summary>The manifest path of the orientation.</summary>
    public const string OrientationField = "outputs.pdf.orientation";

    /// <summary>The manifest path of the margins.</summary>
    public const string MarginField = "outputs.pdf.marginMm";

    /// <summary>The manifest path of one margin.</summary>
    /// <param name="side">The margin's JSON member: <c>top</c>, <c>right</c>, <c>bottom</c> or <c>left</c>.</param>
    public static string MarginFieldOf(string side) => MarginField + "." + side;

    /// <summary>Reads the page settings of a manifest's <c>outputs.pdf</c> element.</summary>
    /// <param name="outputsPdf">The stored <c>outputs.pdf</c> element, or an absent element.</param>
    public static PdfPageSettingsResult Resolve(JsonElement outputsPdf)
    {
        if (outputsPdf.ValueKind != JsonValueKind.Object
            || !outputsPdf.TryGetProperty("paper", out var paper)
            || paper.ValueKind != JsonValueKind.String
            || !PaperSizes.TryMillimetres(paper.GetString(), out var portraitWidth, out var portraitHeight))
        {
            return PdfPageSettingsResult.Unusable(PaperField);
        }

        if (!outputsPdf.TryGetProperty("orientation", out var orientation)
            || orientation.ValueKind != JsonValueKind.String
            || !PageOrientations.TryParse(orientation.GetString(), out var parsedOrientation))
        {
            return PdfPageSettingsResult.Unusable(OrientationField);
        }

        if (!outputsPdf.TryGetProperty("marginMm", out var margins) || margins.ValueKind != JsonValueKind.Object)
        {
            return PdfPageSettingsResult.Unusable(MarginField);
        }

        if (!TryMargin(margins, "top", out var top))
        {
            return PdfPageSettingsResult.Unusable(MarginFieldOf("top"));
        }

        if (!TryMargin(margins, "right", out var right))
        {
            return PdfPageSettingsResult.Unusable(MarginFieldOf("right"));
        }

        if (!TryMargin(margins, "bottom", out var bottom))
        {
            return PdfPageSettingsResult.Unusable(MarginFieldOf("bottom"));
        }

        if (!TryMargin(margins, "left", out var left))
        {
            return PdfPageSettingsResult.Unusable(MarginFieldOf("left"));
        }

        var landscape = parsedOrientation == PageOrientation.Landscape;
        var width = landscape ? portraitHeight : portraitWidth;
        var height = landscape ? portraitWidth : portraitHeight;

        if (width - left - right < MinimumPrintableMm)
        {
            return PdfPageSettingsResult.Unusable(MarginField);
        }

        if (height - top - bottom < MinimumPrintableMm)
        {
            return PdfPageSettingsResult.Unusable(MarginField);
        }

        return PdfPageSettingsResult.Of(new PdfPageSettings(
            paper.GetString()!,
            parsedOrientation,
            width,
            height,
            top,
            right,
            bottom,
            left));
    }

    private static bool TryMargin(JsonElement margins, string side, out decimal value)
    {
        value = 0m;
        return margins.TryGetProperty(side, out var member)
            && member.ValueKind == JsonValueKind.Number
            && member.TryGetDecimal(out value)
            && value >= 0m;
    }
}
