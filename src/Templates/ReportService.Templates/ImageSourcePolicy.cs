namespace ReportService.Templates;

/// <summary>
/// How the output being composed treats the source of an image that is a relative
/// reference. An image data URI passes on every output, and every other value fails.
/// </summary>
public enum ImageSourcePolicy
{
    /// <summary>The printed page: a relative reference is refused, because no rule governs it there.</summary>
    Pdf,

    /// <summary>The HTML output: a relative reference passes, and the HTML assembly inlines a packaged asset or refuses it.</summary>
    Html,

    /// <summary>The Markdown output: a relative reference passes, because an image is written as its alt text only.</summary>
    Markdown,
}
