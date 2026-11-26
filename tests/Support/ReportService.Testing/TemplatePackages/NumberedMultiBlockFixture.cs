namespace ReportService.Testing.TemplatePackages;

/// <summary>
/// The multi-block fixture's manifest transformation: the shipped sample's YAML with
/// an <c>outputs.pdf.numbering</c> contract inserted after its <c>pdf:</c> key. The
/// insertion is line-ending-agnostic: it works on lines and re-joins with the newline
/// the file itself uses. The sample manifests are LF-only while
/// <see cref="Environment.NewLine"/> is CRLF on Windows, so an anchored replacement
/// silently did nothing and the covering run took a package with no contract, which the
/// merge worker correctly refused.
/// </summary>
public static class NumberedMultiBlockFixture
{
    /// <summary>The sample the fixture package is built from.</summary>
    public const string Sample = "pilot-simple";

    /// <summary>The font file the contract names, relative to the package root.</summary>
    public const string FontPath = "fonts/LiberationSans-Regular.ttf";

    /// <summary>The wording the contract declares.</summary>
    public const string Format = "Page {X} of {Y}";

    /// <summary>The culture the contract declares.</summary>
    public const string Culture = "en-US";

    /// <summary>
    /// The sample manifest with the numbering contract inserted after its <c>pdf:</c> key.
    /// </summary>
    /// <param name="yaml">The sample manifest's text.</param>
    /// <returns>The manifest with <c>outputs.pdf.numbering</c> inserted.</returns>
    /// <exception cref="InvalidOperationException">The manifest has no <c>pdf:</c> key to carry the contract.</exception>
    public static string InsertNumberingContract(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var newline = NewlineOf(yaml);
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var pdf = lines.FindIndex(line => string.Equals(line.Trim(), "pdf:", StringComparison.Ordinal));
        if (pdf < 0)
        {
            throw new InvalidOperationException(
                "the multi-block fixture's sample manifest has no pdf: key to carry the numbering contract");
        }

        var indent = lines[pdf].Length - lines[pdf].TrimStart().Length;
        var child = new string(' ', indent + 2);
        var grandchild = new string(' ', indent + 4);
        lines.InsertRange(pdf + 1, new[]
        {
            child + "numbering:",
            grandchild + "area: { leftMm: 120, topMm: 268, widthMm: 80, heightMm: 8 }",
            grandchild + "font: { family: \"Liberation Sans\", file: \"" + FontPath + "\", sizePt: 9 }",
            grandchild + "format: \"" + Format + "\"",
            grandchild + "culture: \"" + Culture + "\"",
        });
        return string.Join(newline, lines);
    }

    /// <summary>The newline a text uses: CRLF when it holds one, LF otherwise.</summary>
    /// <param name="text">The text.</param>
    public static string NewlineOf(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
