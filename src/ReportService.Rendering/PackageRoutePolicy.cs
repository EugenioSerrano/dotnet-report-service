using ReportService.Templates;

namespace ReportService.Rendering;

/// <summary>What a request gets: the document, a file of the package, or nothing.</summary>
/// <param name="Kind">Which of the three it is.</param>
/// <param name="PackagePath">The package-relative path of a served file.</param>
public sealed record RouteDecision(RouteKind Kind, string? PackagePath = null)
{
    /// <summary>Serve the composed document.</summary>
    public static RouteDecision Document { get; } = new(RouteKind.Document);

    /// <summary>Abort the request.</summary>
    public static RouteDecision Abort { get; } = new(RouteKind.Abort);

    /// <summary>Serve a file of the package.</summary>
    public static RouteDecision Serve(string packagePath) => new(RouteKind.Served, packagePath);
}

/// <summary>What happens to one request of a document.</summary>
public enum RouteKind
{
    /// <summary>The composed document itself.</summary>
    Document,

    /// <summary>A file of the package.</summary>
    Served,

    /// <summary>Nothing: the request is aborted and counted.</summary>
    Abort,
}

/// <summary>
/// Decides what a page may fetch. Every request is answered from memory: the
/// document, and the files of the package the package-relative path names. A
/// request for anything else — another origin, a path outside the package, a
/// query, a fragment — is aborted, so a template or a data value reaches nothing.
/// </summary>
public sealed class PackageRoutePolicy(ITemplatePackageFiles files)
{
    /// <summary>The one origin a document and its files are served from.</summary>
    public const string Origin = "https://report.invalid";

    /// <summary>The path of the composed document.</summary>
    public const string DocumentPath = "/document.html";

    /// <summary>The URL of the composed document.</summary>
    public static string DocumentUrl => Origin + DocumentPath;

    /// <summary>The decision for <paramref name="url"/>.</summary>
    public RouteDecision Decide(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith(Origin, StringComparison.Ordinal))
        {
            return RouteDecision.Abort;
        }

        var rest = url[Origin.Length..];
        if (rest.Length == 0 || rest[0] != '/')
        {
            return RouteDecision.Abort;
        }

        if (rest.Contains('?', StringComparison.Ordinal) || rest.Contains('#', StringComparison.Ordinal))
        {
            return RouteDecision.Abort;
        }

        if (string.Equals(rest, DocumentPath, StringComparison.Ordinal))
        {
            return RouteDecision.Document;
        }

        var path = rest[1..];
        try
        {
            return files.Exists(path) ? RouteDecision.Serve(path) : RouteDecision.Abort;
        }
        catch (TemplatePackageException)
        {
            return RouteDecision.Abort;
        }
    }
}
