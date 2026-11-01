using System.Text.RegularExpressions;

namespace ReportService.TemplatePackages.Packages;

/// <summary>
/// The package-relative path of one file inside a template package. A path is
/// the only thing a package says about where its files are, so it is checked
/// before anything is stored and again before anything is extracted: one to
/// eight segments of ASCII letters, digits, <c>.</c>, <c>_</c> and <c>-</c>,
/// none of them <c>.</c> or <c>..</c>, never absolute and never longer than the
/// archive can hold.
/// </summary>
public static partial class PackagePath
{
    /// <summary>The most segments a package path may have.</summary>
    public const int MaxSegments = 8;

    /// <summary>The longest segment, in characters.</summary>
    public const int MaxSegmentLength = 128;

    /// <summary>The longest package path, in characters.</summary>
    public const int MaxLength = 240;

    /// <summary>The name the manifest of every package must carry at its root.</summary>
    public const string ManifestFileName = "template.yaml";

    /// <summary>True when <paramref name="path"/> may name a file of a package.</summary>
    /// <param name="path">The path, with <c>/</c> separators.</param>
    public static bool IsValid(string? path) => IsValid(path, out _);

    /// <summary>True when <paramref name="path"/> may name a file of a package.</summary>
    /// <param name="path">The path, with <c>/</c> separators.</param>
    /// <param name="reason">Why the path is refused; empty when it is accepted.</param>
    public static bool IsValid(string? path, out string reason)
    {
        if (string.IsNullOrEmpty(path))
        {
            reason = "the path is empty";
            return false;
        }

        if (path.Length > MaxLength)
        {
            reason = "the path is longer than 240 characters";
            return false;
        }

        if (path[0] == '/' || path.Contains('\\', StringComparison.Ordinal))
        {
            reason = "the path is absolute or uses a backslash";
            return false;
        }

        var segments = path.Split('/');
        if (segments.Length > MaxSegments)
        {
            reason = "the path has more than 8 segments";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                reason = "the path has a '.' or '..' segment";
                return false;
            }

            if (segment.Length is 0 or > MaxSegmentLength || !Segment().IsMatch(segment))
            {
                reason = "a segment is empty, too long or holds a character other than A-Z, a-z, 0-9, '.', '_' and '-'";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();
}
