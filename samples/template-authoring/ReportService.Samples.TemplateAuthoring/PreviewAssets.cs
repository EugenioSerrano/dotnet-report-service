using System.Net;
using System.Text.RegularExpressions;
using ReportService.Rendering;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// The package files a printed document reaches, found by the printer's own rule: every
/// reference of the document is resolved against the document's URL, every reference
/// inside a reached stylesheet against that stylesheet's URL, and a file is reached only
/// when the print's route policy would serve it. Writing these files beside the preview,
/// at their package-relative paths, lets a browser open the preview on its own and read
/// the same files the print was served.
/// </summary>
public static partial class PreviewAssets
{
    /// <summary>The package-relative paths the document reaches, each once, in the order first reached.</summary>
    /// <param name="document">The document handed to the printer.</param>
    /// <param name="files">The package's files.</param>
    public static IReadOnlyList<string> Reached(string document, ITemplatePackageFiles files)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(files);

        var policy = new PackageRoutePolicy(files);
        var reached = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(Uri Url, bool Stylesheet)>();
        var documentUrl = new Uri(PackageRoutePolicy.DocumentUrl);

        foreach (Match match in LinkElement().Matches(document))
        {
            var tag = match.Value;
            var rel = Attribute(tag, "rel");
            var href = Attribute(tag, "href");
            if (href is null || rel is null
                || !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token => string.Equals(token, "stylesheet", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Enqueue(documentUrl, href, stylesheet: true);
        }

        foreach (Match match in SourceAttribute().Matches(document))
        {
            Enqueue(documentUrl, WebUtility.HtmlDecode(match.Groups["value"].Value), stylesheet: false);
        }

        foreach (Match match in StyleElement().Matches(document))
        {
            foreach (var reference in CssReferences(match.Groups["css"].Value))
            {
                Enqueue(documentUrl, reference.Value, reference.Stylesheet);
            }
        }

        while (pending.Count > 0)
        {
            var (url, stylesheet) = pending.Dequeue();
            var decision = policy.Decide(url.AbsoluteUri);
            if (decision.Kind != RouteKind.Served || !seen.Add(decision.PackagePath!))
            {
                continue;
            }

            reached.Add(decision.PackagePath!);
            if (stylesheet)
            {
                foreach (var reference in CssReferences(files.ReadAllText(decision.PackagePath!)))
                {
                    Enqueue(url, reference.Value, reference.Stylesheet);
                }
            }
        }

        return reached;

        void Enqueue(Uri baseUrl, string reference, bool stylesheet)
        {
            var value = reference.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                return;
            }

            if (Uri.TryCreate(baseUrl, value, out var url))
            {
                pending.Enqueue((url, stylesheet));
            }
        }
    }

    /// <summary>
    /// Writes each reached file beside the preview, at its package-relative path under
    /// <paramref name="outputDirectory"/>, replacing a previous run's copy.
    /// </summary>
    /// <param name="paths">The reached package-relative paths.</param>
    /// <param name="files">The package's files.</param>
    /// <param name="outputDirectory">The preview's folder.</param>
    public static void WriteBeside(IReadOnlyList<string> paths, ITemplatePackageFiles files, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var root = Path.GetFullPath(outputDirectory);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        foreach (var path in paths)
        {
            var target = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = files.OpenRead(path);
            using var destination = File.Create(target);
            source.CopyTo(destination);
        }
    }

    private static IEnumerable<(string Value, bool Stylesheet)> CssReferences(string css)
    {
        var text = CssComment().Replace(css, string.Empty);
        foreach (Match match in CssImport().Matches(text))
        {
            yield return (match.Groups["value"].Value, true);
        }

        foreach (Match match in CssUrl().Matches(text))
        {
            var isImport = CssImportUrlPrefix().IsMatch(text[..match.Index]);
            if (!isImport)
            {
                yield return (match.Groups["value"].Value, false);
            }
        }
    }

    private static string? Attribute(string tag, string name)
    {
        foreach (Match match in TagAttribute().Matches(tag))
        {
            if (string.Equals(match.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                var value = match.Groups["double"].Success ? match.Groups["double"].Value
                    : match.Groups["single"].Success ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
                return WebUtility.HtmlDecode(value);
            }
        }

        return null;
    }

    [GeneratedRegex("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LinkElement();

    [GeneratedRegex("<(?:img|source|image)\\b[^>]*?\\s(?:src|href)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SourceAttribute();

    [GeneratedRegex("<style\\b[^>]*>(?<css>.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StyleElement();

    [GeneratedRegex("(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s\"'>]+))", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TagAttribute();

    [GeneratedRegex("/\\*.*?\\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CssComment();

    [GeneratedRegex("@import\\s+(?:url\\(\\s*)?(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s\"');]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CssImport();

    [GeneratedRegex("url\\(\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s\"')]+))\\s*\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CssUrl();

    [GeneratedRegex("@import\\s+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CssImportUrlPrefix();
}
