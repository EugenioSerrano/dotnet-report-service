using System.Text;
using ReportService.Templates;

namespace ReportService.Rendering.Tests;

/// <summary>
/// The route policy: the document and the package's own files are served from
/// memory; anything else — another origin, a path outside the package, a query, a
/// file the package does not hold — is aborted.
/// </summary>
public sealed class PackageRoutePolicyTests
{
    private static readonly PackageRoutePolicy Policy = new(new Package(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["styles.css"] = "body{}",
        ["fonts/LiberationSans.woff2"] = "font",
    }));

    [Fact]
    public void TheDocumentIsServed()
    {
        var decision = Policy.Decide(PackageRoutePolicy.DocumentUrl);

        Assert.Equal(RouteKind.Document, decision.Kind);
        Assert.Equal("https://report.invalid/document.html", PackageRoutePolicy.DocumentUrl);
    }

    [Fact]
    public void AFileOfThePackageIsServed()
    {
        var decision = Policy.Decide("https://report.invalid/styles.css");

        Assert.Equal(RouteKind.Served, decision.Kind);
        Assert.Equal("styles.css", decision.PackagePath);

        var font = Policy.Decide("https://report.invalid/fonts/LiberationSans.woff2");
        Assert.Equal(RouteKind.Served, font.Kind);
        Assert.Equal("fonts/LiberationSans.woff2", font.PackagePath);
    }

    [Theory]
    [InlineData("https://example.com/styles.css")]
    [InlineData("http://report.invalid/styles.css")]
    [InlineData("https://report.invalid.evil.example/styles.css")]
    [InlineData("https://report.invalid/styles.css?v=2")]
    [InlineData("https://report.invalid/styles.css#top")]
    [InlineData("https://report.invalid/../etc/passwd")]
    [InlineData("https://report.invalid/missing.css")]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/css,body{}")]
    [InlineData("")]
    public void EverythingElseIsAborted(string url)
    {
        Assert.Equal(RouteKind.Abort, Policy.Decide(url).Kind);
    }

    [Fact]
    public void AnOriginWithoutAPathIsAborted()
    {
        Assert.Equal(RouteKind.Abort, Policy.Decide("https://report.invalid").Kind);
        Assert.Equal(RouteKind.Abort, Policy.Decide("https://report.invalidx/styles.css").Kind);
    }

    [Fact]
    public void ANameThePackageRefusesIsAbortedRatherThanServed()
    {
        var policy = new PackageRoutePolicy(new Package(new Dictionary<string, string>(StringComparer.Ordinal)));

        Assert.Equal(RouteKind.Abort, policy.Decide("https://report.invalid/%2e%2e/secret").Kind);
    }

    [Theory]
    [InlineData("styles.css", "text/css; charset=utf-8")]
    [InlineData("page.html", "text/html; charset=utf-8")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("logo.png", "image/png")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("icon.gif", "image/gif")]
    [InlineData("font.woff", "font/woff")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("font.ttf", "font/ttf")]
    [InlineData("font.otf", "font/otf")]
    [InlineData("other.bin", "application/octet-stream")]
    [InlineData("UPPER.CSS", "text/css; charset=utf-8")]
    public void AFileIsServedWithTheMediaTypeOfItsName(string name, string mediaType)
    {
        Assert.Equal(mediaType, ChromiumPdfPrinter.MediaTypeOf(name));
    }

    [Fact]
    public void TheDocumentCarriesAContentSecurityPolicyThatAllowsNothing()
    {
        Assert.Contains("default-src 'none'", ChromiumPdfPrinter.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("style-src https://report.invalid", ChromiumPdfPrinter.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("font-src https://report.invalid", ChromiumPdfPrinter.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", ChromiumPdfPrinter.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    /// <summary>A package held in memory, so the policy is decided without a folder.</summary>
    private sealed class Package(IReadOnlyDictionary<string, string> files) : ITemplatePackageFiles
    {
        public bool Exists(string path) =>
            !path.Contains("..", StringComparison.Ordinal) && files.ContainsKey(path);

        public Stream OpenRead(string path) => new MemoryStream(Encoding.UTF8.GetBytes(files[path]));

        public string ReadAllText(string path) => files[path];
    }
}
