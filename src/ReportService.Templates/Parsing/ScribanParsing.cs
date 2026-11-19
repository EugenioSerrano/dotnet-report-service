using Scriban;

namespace ReportService.Templates;

/// <summary>
/// The one Scriban parse the product makes. The take's validator and the render's
/// adapter both come through here, with the library's default parser and lexer options,
/// so a part the take accepts is one the render can prepare.
/// </summary>
internal static class ScribanParsing
{
    /// <summary>Parses one part with the product's configuration.</summary>
    /// <param name="path">The package-relative path, used only as a name in diagnostics.</param>
    /// <param name="text">The part's text.</param>
    public static Template Parse(string path, string text) =>
        Template.Parse(text, path, parserOptions: null, lexerOptions: null);
}
