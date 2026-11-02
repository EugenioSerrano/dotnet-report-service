namespace ReportService.Templates;

/// <summary>
/// The subset of a taken package's manifest the renderer needs. It is mapped
/// from the package record kept at take time; nothing here is read from the
/// template source.
/// </summary>
/// <param name="Engine">The engine the package declares, as the manifest names it.</param>
/// <param name="EngineVersion">The engine version the package declares.</param>
/// <param name="Parts">The part paths of the output being composed, by part.</param>
/// <param name="Stylesheets">The package-relative stylesheet paths, in the order the package lists them.</param>
/// <param name="Pdf">The PDF settings of the package.</param>
/// <param name="LocaleDefaults">The package's fallback culture and time zone.</param>
/// <param name="GroupKey">The record field that begins a group, or <see langword="null"/> when the package declares none.</param>
public sealed record RenderManifest(
    string Engine,
    string EngineVersion,
    IReadOnlyDictionary<TemplatePart, string> Parts,
    IReadOnlyList<string> Stylesheets,
    PdfSettings Pdf,
    LocaleDefaults LocaleDefaults,
    string? GroupKey)
{
    /// <summary>The path of <paramref name="part"/>, or <see langword="null"/> when the package declares none.</summary>
    public string? PathOf(TemplatePart part) => Parts.TryGetValue(part, out var path) ? path : null;

    /// <summary>Whether the package declares <paramref name="part"/>.</summary>
    public bool Has(TemplatePart part) => Parts.ContainsKey(part);

    /// <summary>Whether the package declares page subtotals, which this increment cannot print.</summary>
    public bool DeclaresPageTotals => Pdf.PageTotalsEnabled;
}

/// <summary>The PDF settings a package declares.</summary>
/// <param name="Paper">The paper size, as the manifest names it.</param>
/// <param name="Orientation">The page orientation, as the manifest names it.</param>
/// <param name="MarginMm">The declared margins, in millimetres.</param>
/// <param name="PageTotalsEnabled">Whether the package declares page subtotals.</param>
/// <param name="Verification">The fields the package prints as they are, or <see langword="null"/> when the manifest does not declare them.</param>
public sealed record PdfSettings(
    string Paper,
    string Orientation,
    PageMarginsMm MarginMm,
    bool PageTotalsEnabled,
    VerificationAnchors? Verification);

/// <summary>The declared margins of a page, in millimetres.</summary>
public sealed record PageMarginsMm(double Top, double Right, double Bottom, double Left);

/// <summary>
/// The record and header fields a package prints as they are, as member paths.
/// A package that declares a PDF output must declare them: an absent
/// declaration fails the block instead of being read as "no fields".
/// </summary>
public sealed record VerificationAnchors(IReadOnlyList<string> Header, IReadOnlyList<string> Item)
{
    /// <summary>No field is declared: an explicitly empty declaration, which is honoured.</summary>
    public static VerificationAnchors None { get; } = new([], []);
}

/// <summary>The package's fallback culture and time zone, used when the request names none.</summary>
public sealed record LocaleDefaults(string? Culture, string? TimeZone);
