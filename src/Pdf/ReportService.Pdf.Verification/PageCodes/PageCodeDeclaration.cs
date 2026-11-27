using System.Text.RegularExpressions;

namespace ReportService.Pdf.Verification.PageCodes;

/// <summary>The area a template reserves for its per-page code, in millimetres from the page's top-left corner.</summary>
/// <param name="LeftMm">The distance from the page's left edge.</param>
/// <param name="TopMm">The distance from the page's top edge.</param>
/// <param name="WidthMm">The area's width.</param>
/// <param name="HeightMm">The area's height.</param>
public sealed record PageCodeRectangle(double LeftMm, double TopMm, double WidthMm, double HeightMm)
{
    /// <summary>Points per millimetre.</summary>
    public const double PointsPerMillimetre = 72d / 25.4d;

    /// <summary>The area in page points.</summary>
    public PageCodeText.Region Region => new(
        LeftMm * PointsPerMillimetre,
        TopMm * PointsPerMillimetre,
        WidthMm * PointsPerMillimetre,
        HeightMm * PointsPerMillimetre);

    /// <summary>The area's size, as the generator receives it.</summary>
    public PageCodeArea Size => new(WidthMm * PointsPerMillimetre, HeightMm * PointsPerMillimetre);
}

/// <summary>A template's per-page code: the generator it names, its options, its area and its value source.</summary>
/// <param name="Generator">The registered generator's name.</param>
/// <param name="Options">The options passed unchanged to the generator.</param>
/// <param name="Area">The area.</param>
/// <param name="Source">The value source.</param>
public sealed record PageCodeDeclaration(
    string Generator,
    IReadOnlyDictionary<string, string> Options,
    PageCodeRectangle Area,
    PageCodeSource Source)
{
    /// <summary>The smallest area side, in millimetres.</summary>
    public const double MinimumSideMm = 5d;

    /// <summary>The largest area side, in millimetres.</summary>
    public const double MaximumSideMm = 100d;

    /// <summary>The most options a declaration may carry.</summary>
    public const int MaximumOptions = 8;

    private static readonly Regex GeneratorName = new("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex OptionKey = new("^[a-zA-Z0-9]{1,32}$", RegexOptions.CultureInvariant);

    /// <summary>The raw values a manifest declares, before the rules apply.</summary>
    /// <param name="Generator">The generator's name.</param>
    /// <param name="Options">The options, when declared.</param>
    /// <param name="LeftMm">The area's left.</param>
    /// <param name="TopMm">The area's top.</param>
    /// <param name="WidthMm">The area's width.</param>
    /// <param name="HeightMm">The area's height.</param>
    /// <param name="Value">The value template.</param>
    /// <param name="Pattern">The match pattern.</param>
    /// <param name="MaxLength">The match's declared longest length.</param>
    public sealed record Raw(
        string? Generator,
        IReadOnlyDictionary<string, string>? Options,
        double? LeftMm,
        double? TopMm,
        double? WidthMm,
        double? HeightMm,
        string? Value,
        string? Pattern,
        int? MaxLength);

    /// <summary>Applies the declaration's own rules, which need nothing but the declaration.</summary>
    /// <param name="raw">The declared values.</param>
    /// <param name="problems">Where problems are added.</param>
    /// <returns>The declaration, or <see langword="null"/> when it has problems.</returns>
    public static PageCodeDeclaration? Create(Raw raw, ICollection<PageCodeProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(problems);
        var before = problems.Count;
        if (raw.Generator is null || !GeneratorName.IsMatch(raw.Generator))
        {
            problems.Add(new PageCodeProblem(
                "generator",
                "the generator name is missing or is not 1 to 64 characters of a-z, 0-9 and '-'",
                "name a page-code generator registered in the deployment"));
        }

        var options = raw.Options ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.Count > MaximumOptions
            || options.Any(option => !OptionKey.IsMatch(option.Key) || option.Value is null || option.Value.Length > 64))
        {
            problems.Add(new PageCodeProblem(
                "options",
                "the options are more than " + MaximumOptions + ", or a key is not 1 to 32 letters and digits, or a value is longer than 64 characters",
                "keep at most " + MaximumOptions + " options with short keys and string values"));
        }

        if (raw.LeftMm is not { } left || raw.TopMm is not { } top || raw.WidthMm is not { } width || raw.HeightMm is not { } height
            || left < 0 || top < 0
            || width < MinimumSideMm || width > MaximumSideMm || height < MinimumSideMm || height > MaximumSideMm)
        {
            problems.Add(new PageCodeProblem(
                "area",
                "the area is missing, starts before the page's edge, or a side is outside " + MinimumSideMm + " to " + MaximumSideMm + " mm",
                "declare area.leftMm and area.topMm of 0 or more and sides of " + MinimumSideMm + " to " + MaximumSideMm + " mm"));
            left = top = width = height = 0;
        }

        var source = PageCodeSource.Parse(raw.Value, raw.Pattern, raw.MaxLength, problems);
        return problems.Count == before && source is not null
            ? new PageCodeDeclaration(raw.Generator!, options, new PageCodeRectangle(left, top, width, height), source)
            : null;
    }
}
