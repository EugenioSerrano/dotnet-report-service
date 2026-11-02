using Microsoft.Playwright;
using ReportService.Templates;

namespace ReportService.Rendering;

/// <summary>What one print produced.</summary>
/// <param name="Path">The PDF file the print wrote.</param>
/// <param name="AbortedRequests">How many requests the route policy aborted.</param>
/// <param name="ConsoleErrors">How many console errors the page reported.</param>
/// <param name="Duration">How long the print took.</param>
public sealed record PrintOutcome(string Path, int AbortedRequests, int ConsoleErrors, TimeSpan Duration);

/// <summary>A print that may succeed when it is attempted again: a page crash, or a browser that went away.</summary>
public sealed class PrintTemporarilyFailedException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Prints one composed block with Chromium's own page layout.
/// <para>
/// Every print gets a new browser context with script execution disabled; every
/// request of the document is answered from memory — the document itself with a
/// content security policy, the package's own files, nothing else — and every
/// other request is aborted and counted. The document is printed with the page
/// size its stylesheet declares, backgrounds included, and no header, footer,
/// outline or tag tree of the browser's own.
/// </para>
/// </summary>
public sealed class ChromiumPdfPrinter(BrowserSession session, TimeProvider clock)
{
    /// <summary>The content security policy the document carries.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src https://report.invalid; font-src https://report.invalid; img-src https://report.invalid data:";

    /// <summary>How long the fonts of a document are given to load before the print goes ahead.</summary>
    public static readonly TimeSpan FontsWaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>Prints <paramref name="html"/> to <paramref name="pdfPath"/>.</summary>
    public async Task<PrintOutcome> PrintAsync(
        string html,
        ITemplatePackageFiles files,
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        var started = clock.GetTimestamp();
        var policy = new PackageRoutePolicy(files);
        var aborted = 0;
        var consoleErrors = 0;

        var browser = await session.GetBrowserAsync(cancellationToken).ConfigureAwait(false);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false }).ConfigureAwait(false);
        try
        {
            var page = await context.NewPageAsync().ConfigureAwait(false);
            page.Console += (_, message) =>
            {
                if (string.Equals(message.Type, "error", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref consoleErrors);
                }
            };
            page.Crash += (_, _) => Interlocked.Increment(ref consoleErrors);

            await page.RouteAsync("**/*", async route =>
            {
                var decision = policy.Decide(route.Request.Url);
                switch (decision.Kind)
                {
                    case RouteKind.Document:
                        await route.FulfillAsync(new RouteFulfillOptions
                        {
                            Status = 200,
                            ContentType = "text/html; charset=utf-8",
                            Body = html,
                            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Security-Policy"] = ContentSecurityPolicy },
                        }).ConfigureAwait(false);
                        break;
                    case RouteKind.Served:
                        await FulfillPackageFileAsync(route, files, decision.PackagePath!).ConfigureAwait(false);
                        break;
                    default:
                        Interlocked.Increment(ref aborted);
                        await route.AbortAsync().ConfigureAwait(false);
                        break;
                }
            }).ConfigureAwait(false);

            await page.GotoAsync(
                PackageRoutePolicy.DocumentUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);
            await WaitForFontsAsync(page, cancellationToken).ConfigureAwait(false);

            await page.PdfAsync(new PagePdfOptions
            {
                PreferCSSPageSize = true,
                PrintBackground = true,
                DisplayHeaderFooter = false,
                Tagged = false,
                Outline = false,
                Path = pdfPath,
            }).ConfigureAwait(false);

            return new PrintOutcome(pdfPath, aborted, consoleErrors, clock.GetElapsedTime(started));
        }
        catch (PlaywrightException exception)
        {
            throw new PrintTemporarilyFailedException($"The document could not be printed: {exception.Message}", exception);
        }
        finally
        {
            await context.CloseAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task FulfillPackageFileAsync(IRoute route, ITemplatePackageFiles files, string packagePath)
    {
        using var stream = files.OpenRead(packagePath);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        await route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = MediaTypeOf(packagePath),
            BodyBytes = buffer.ToArray(),
        }).ConfigureAwait(false);
    }

    /// <summary>The media type of a package file, from its name.</summary>
    public static string MediaTypeOf(string packagePath) =>
        Path.GetExtension(packagePath).ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".html" => "text/html; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => "application/octet-stream",
        };

    /// <summary>
    /// Waits until the document's fonts have loaded. Script execution is disabled
    /// for the document, so the engine's own readiness check is asked for first and
    /// the document's load state is the fallback when the check cannot run.
    /// </summary>
    private static async Task WaitForFontsAsync(IPage page, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(FontsWaitLimit);
        try
        {
            await page.WaitForFunctionAsync(
                "() => document.fonts.status === 'loaded'",
                arg: null,
                new PageWaitForFunctionOptions { PollingInterval = 50, Timeout = (float)FontsWaitLimit.TotalMilliseconds })
                .ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            await page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        }
    }
}
