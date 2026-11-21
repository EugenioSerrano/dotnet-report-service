using System.Text.RegularExpressions;

namespace ReportService.TemplatePackages.Versioning;

/// <summary>
/// Whether a string is a semantic version's <em>syntax</em>: three numeric
/// parts without leading zeros, an optional pre-release of dot-separated
/// identifiers and an optional build of dot-separated identifiers, all ASCII
/// and at most 128 characters. This check is syntax only;
/// <see cref="SemanticVersion"/> decides precedence and is what a resolution
/// orders versions with.
/// </summary>
public static partial class SemanticVersionSyntax
{
    /// <summary>The longest version this service records.</summary>
    public const int MaxLength = 128;

    /// <summary>True when <paramref name="version"/> is a semantic version by its syntax alone.</summary>
    /// <param name="version">The version as written.</param>
    public static bool IsValid(string? version) =>
        version is not null
        && version.Length <= MaxLength
        && Syntax().IsMatch(version);

    [GeneratedRegex(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Syntax();
}
