namespace ReportService.Templates;

/// <summary>
/// The one include-name resolution the Scriban render uses: an include names a
/// package-relative path, and the render loads it only when that path is one of
/// the package's declared parts. The take's include check applies the same rule,
/// so "the take accepted it" and "the render loads it" cannot drift.
/// </summary>
public static class ScribanIncludes
{
    /// <summary>
    /// The package path an include name spells, or <see langword="null"/> when the
    /// name is empty. Whether the path is a declared part is the caller's check:
    /// the render's loader serves the parts, and the take names the file when it
    /// does not.
    /// </summary>
    /// <param name="name">The include name, as the file writes it.</param>
    public static string? Resolve(string? name) =>
        string.IsNullOrEmpty(name) ? null : name;
}
