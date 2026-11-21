namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// The closed vocabulary the PDF constraint rules read: the generic font families
/// of CSS Fonts 4, the CSS-wide keywords and reference functions of CSS Cascade 5,
/// the block-axis end values of CSS Box Alignment 3, the grid container values, the
/// break-avoiding property and value, the properties the rules are about, and the
/// elements whose content the page never paints. A declaration of any other
/// property is not about these constraints and carries no weight; a value outside
/// these lists is not one of the declarations a rule refuses. No logic beyond
/// membership, so a change to a rule is one edit here.
/// </summary>
public static class PdfConstraintRules
{
    /// <summary>The property that names the font family of text.</summary>
    public const string FontFamilyProperty = "font-family";

    /// <summary>The shorthand whose value may carry a family beside a size and a weight.</summary>
    public const string FontShorthandProperty = "font";

    /// <summary>The property that makes an element a grid container.</summary>
    public const string DisplayProperty = "display";

    /// <summary>The generic family keywords of CSS Fonts 4: none of them names a typeface.</summary>
    public static IReadOnlySet<string> GenericFontFamilies { get; } = Set(
        "serif",
        "sans-serif",
        "monospace",
        "cursive",
        "fantasy",
        "system-ui",
        "ui-serif",
        "ui-sans-serif",
        "ui-monospace",
        "ui-rounded",
        "math",
        "emoji",
        "fangsong");

    /// <summary>The CSS-wide keywords of CSS Cascade 5: a keyword is not a typeface.</summary>
    public static IReadOnlySet<string> CssWideKeywords { get; } = Set(
        "inherit",
        "initial",
        "unset",
        "revert",
        "revert-layer");

    /// <summary>The reference functions whose value is not in the package's own text.</summary>
    public static IReadOnlySet<string> ReferenceFunctions { get; } = Set("var", "env");

    /// <summary>
    /// The block-axis alignment values of CSS Box Alignment 3 that put a grid item
    /// at the bottom, with and without the <c>safe</c> and <c>unsafe</c> prefixes.
    /// The baseline values are deliberately absent: no measurement puts a lone grid
    /// item at the bottom through them.
    /// </summary>
    public static IReadOnlySet<string> BottomAlignmentValues { get; } = Set(
        "end",
        "flex-end",
        "self-end",
        "safe end",
        "safe flex-end",
        "safe self-end",
        "unsafe end",
        "unsafe flex-end",
        "unsafe self-end");

    /// <summary>The display values that make an element a grid container.</summary>
    public static IReadOnlySet<string> GridContainerValues { get; } = Set("grid", "inline-grid");

    /// <summary>The properties that forbid a page break inside an element.</summary>
    public static IReadOnlySet<string> BreakAvoidingProperties { get; } = Set("break-inside", "page-break-inside");

    /// <summary>The values that forbid a page break.</summary>
    public static IReadOnlySet<string> BreakAvoidingValues { get; } = Set("avoid");

    /// <summary>The alignment properties a bottom-aligned grid item may be declared through.</summary>
    public static IReadOnlySet<string> AlignmentProperties { get; } = Set(
        "align-items",
        "align-self",
        "place-items",
        "place-self");

    /// <summary>The elements whose content the page never paints, whatever it holds.</summary>
    public static IReadOnlySet<string> NonRenderedElements { get; } = Set("script", "style", "template");

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}
