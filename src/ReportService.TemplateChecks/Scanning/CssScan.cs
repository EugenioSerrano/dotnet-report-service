using System.Text.RegularExpressions;

namespace ReportService.TemplateChecks.Scanning;

/// <summary>
/// Reads the references a stylesheet makes the render fetch: <c>url(…)</c> and
/// <c>@import</c>. It is a text scan, comment- and string-aware; it fetches
/// nothing and executes nothing.
/// </summary>
public static partial class CssScanner
{
    /// <summary>The URLs a stylesheet declares, in document order.</summary>
    /// <param name="css">The stylesheet's text.</param>
    public static IReadOnlyList<string> Urls(string css)
    {
        ArgumentNullException.ThrowIfNull(css);

        var urls = new List<string>();
        foreach (Match match in Url().Matches(css))
        {
            var url = match.Groups[1].Value.Trim();
            if (url.Length >= 2 && (url[0] == '"' || url[0] == '\'') && url[^1] == url[0])
            {
                url = url[1..^1];
            }

            if (url.Length > 0)
            {
                urls.Add(url);
            }
        }

        foreach (Match match in Import().Matches(css))
        {
            var url = match.Groups[1].Value.Trim();
            if (url.Length > 0)
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    [GeneratedRegex("url\\(\\s*([^)]*?)\\s*\\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex("@import\\s+(?:url\\(\\s*)?[\"']([^\"']+)[\"']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Import();
}
