namespace ReportService.TemplatePackages.Manifest;

/// <summary>The orientation a PDF template declares for its page.</summary>
public enum PageOrientation
{
    /// <summary>The page's width is the paper's shorter side.</summary>
    Portrait,

    /// <summary>The page's width is the paper's longer side.</summary>
    Landscape,
}

/// <summary>
/// The two orientations a template may declare and the words it names them with.
/// The words are compared without ASCII case, and the canonical word is what the
/// product reports for a page.
/// </summary>
public static class PageOrientations
{
    /// <summary>The word a portrait page is declared with.</summary>
    public const string Portrait = "portrait";

    /// <summary>The word a landscape page is declared with.</summary>
    public const string Landscape = "landscape";

    /// <summary>Reads <paramref name="text"/> as an orientation.</summary>
    /// <param name="text">The word the manifest declares.</param>
    /// <param name="orientation">The orientation it names.</param>
    public static bool TryParse(string? text, out PageOrientation orientation)
    {
        if (PaperSizes.AsciiEquals(text, Portrait))
        {
            orientation = PageOrientation.Portrait;
            return true;
        }

        if (PaperSizes.AsciiEquals(text, Landscape))
        {
            orientation = PageOrientation.Landscape;
            return true;
        }

        orientation = default;
        return false;
    }

    /// <summary>The manifest word of <paramref name="orientation"/>.</summary>
    /// <param name="orientation">The orientation.</param>
    public static string Name(PageOrientation orientation) =>
        orientation == PageOrientation.Landscape ? Landscape : Portrait;
}
