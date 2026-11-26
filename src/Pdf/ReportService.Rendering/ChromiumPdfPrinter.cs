using Microsoft.Playwright;
using ReportService.Rendering.Layout;
using ReportService.Templates;

namespace ReportService.Rendering;

/// <summary>What one print produced.</summary>
/// <param name="Path">The PDF file the print wrote.</param>
/// <param name="AbortedRequests">How many requests the route policy aborted.</param>
/// <param name="ConsoleErrors">How many console errors the page reported.</param>
/// <param name="Duration">How long the print took.</param>
/// <param name="Guard">What the accounting layout reported, or <see langword="null"/> on the native path.</param>
public sealed record PrintOutcome(string Path, int AbortedRequests, int ConsoleErrors, TimeSpan Duration, LayoutGuardReport? Guard = null);

/// <summary>A print that may succeed when it is attempted again: a page crash, or a browser that went away.</summary>
public sealed class PrintTemporarilyFailedException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Prints one composed block with Chromium.
/// <para>
/// Every print gets a new browser context and answers every request of the document
/// from memory — the document itself with a content security policy, the package's own
/// files, nothing else; every other request is aborted and counted. The native path
/// disables script execution and is today's behaviour byte for byte. The accounting
/// path enables it for that context only, allows exactly the three hash-pinned inline
/// scripts, waits for the layout's done signal under a deadline and reads the guard
/// report; the print is never written from a layout the guard refused.
/// </para>
/// </summary>
public sealed class ChromiumPdfPrinter(BrowserSession session, TimeProvider clock) : IPdfPrinter
{
    /// <summary>The content security policy of a document that carries no page rule: nothing is allowed but the package's own origin.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src https://report.invalid; font-src https://report.invalid; img-src https://report.invalid data:";

    /// <summary>
    /// The policy of the accounting print: the three inline scripts by their SHA-256
    /// hashes and no host source, and no <c>'unsafe-inline'</c> in <c>script-src</c>, so
    /// a template's own script cannot run. Inline styles are allowed for this print only
    /// because Paged.js inserts its own styles at run time; the page rule stays the last
    /// style element and <c>!important</c>. The package's own origin is allowed for the
    /// stylesheet reads Paged.js performs, and the route policy answers them from memory.
    /// </summary>
    public static string AccountedPolicy { get; } =
        "default-src 'none'; script-src " + string.Join(' ', LayoutAssets.ScriptHashSources)
        + "; style-src https://report.invalid 'unsafe-inline'; font-src https://report.invalid"
        + "; img-src https://report.invalid data:; connect-src https://report.invalid";

    /// <summary>
    /// The policy of a native document that carries <paramref name="pageRule"/>: the
    /// package's own origin, plus the hash of exactly that rule and nothing else. Inline
    /// styles and style attributes of the package stay blocked.
    /// </summary>
    /// <param name="pageRule">The rule the served document carries.</param>
    public static string PolicyFor(PageRule pageRule)
    {
        ArgumentNullException.ThrowIfNull(pageRule);
        return "default-src 'none'; style-src https://report.invalid " + pageRule.CspHashSource
            + "; font-src https://report.invalid; img-src https://report.invalid data:";
    }

    /// <summary>How long the fonts of a document are given to load before the print goes ahead.</summary>
    public static readonly TimeSpan FontsWaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>How long the accounting layout is given to signal that it is done.</summary>
    public static readonly TimeSpan LayoutWaitLimit = TimeSpan.FromSeconds(120);

    /// <summary>Prints <paramref name="document"/> on the native path.</summary>
    public Task<PrintOutcome> PrintAsync(
        string document,
        PageRule pageRule,
        ITemplatePackageFiles files,
        string pdfPath,
        CancellationToken cancellationToken = default) =>
        PrintAsync(
            new PdfPrintRequest(document, pageRule, files, PdfPrintMode.Native, Accounting: null, pdfPath),
            cancellationToken);

    /// <summary>Prints <paramref name="request"/>.</summary>
    public async Task<PrintOutcome> PrintAsync(PdfPrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentNullException.ThrowIfNull(request.PageRule);
        ArgumentNullException.ThrowIfNull(request.Files);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PdfPath);

        var started = clock.GetTimestamp();
        var policy = new PackageRoutePolicy(request.Files);
        var contentSecurityPolicy = request.IsAccounted ? AccountedPolicy : PolicyFor(request.PageRule);
        var document = request.IsAccounted ? LayoutAssets.AppendTo(request.Document) : request.Document;
        var aborted = 0;
        var consoleErrors = 0;

        var browser = await session.GetBrowserAsync(cancellationToken).ConfigureAwait(false);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = request.IsAccounted }).ConfigureAwait(false);
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
                            Body = document,
                            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Security-Policy"] = contentSecurityPolicy },
                        }).ConfigureAwait(false);
                        break;
                    case RouteKind.Served:
                        await FulfillPackageFileAsync(route, request.Files, decision.PackagePath!).ConfigureAwait(false);
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

            LayoutGuardReport? guard = null;
            if (request.IsAccounted)
            {
                guard = await WaitForAccountingAsync(page, cancellationToken).ConfigureAwait(false);
                if (guard.Breached)
                {
                    return new PrintOutcome(request.PdfPath, aborted, consoleErrors, clock.GetElapsedTime(started), guard);
                }
            }

            await WaitForFontsAsync(page, cancellationToken).ConfigureAwait(false);

            await page.PdfAsync(new PagePdfOptions
            {
                PreferCSSPageSize = true,
                PrintBackground = true,
                DisplayHeaderFooter = false,
                Tagged = false,
                Outline = false,
                Path = request.PdfPath,
            }).ConfigureAwait(false);

            return new PrintOutcome(request.PdfPath, aborted, consoleErrors, clock.GetElapsedTime(started), guard);
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

    /// <summary>
    /// Waits for the layout's done signal and reads the guard report. A layout that
    /// signals a failure, and one that never signals within the deadline, are both a
    /// guard breach: the block prints nothing and the print writes no file.
    /// </summary>
    private static async Task<LayoutGuardReport> WaitForAccountingAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "() => { const v = document.documentElement.dataset.layout; return v === 'done' || v === 'failed'; }",
                arg: null,
                new PageWaitForFunctionOptions
                {
                    PollingInterval = 100,
                    Timeout = (float)LayoutWaitLimit.TotalMilliseconds,
                }).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return LayoutGuardReport.Missing with { Phase = "timeout" };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dto = await page.EvaluateAsync<GuardDto>("() => window.__layoutGuard || null").ConfigureAwait(false);
        var signal = await page.EvaluateAsync<string>("() => document.documentElement.dataset.layout || ''").ConfigureAwait(false);
        var reason = await page.EvaluateAsync<string>("() => document.documentElement.dataset.layoutReason || ''").ConfigureAwait(false);
        var report = dto is null
            ? LayoutGuardReport.Missing
            : new LayoutGuardReport(
                dto.Phase,
                dto.Pages,
                dto.Rows,
                dto.SplitRows,
                dto.MaxParts,
                dto.PartOverLimit,
                dto.UnfinishedLastPage,
                dto.AccountingError,
                dto.Error);
        return string.Equals(signal, "done", StringComparison.Ordinal)
            ? report
            : report with
            {
                Phase = report.Phase is null or "done" ? "failed" : report.Phase,
                Error = report.Error ?? (reason.Length > 0 ? reason : "layout-not-done"),
            };
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
    /// Waits until the document's fonts have loaded. On the native path script execution
    /// is disabled for the document, so the engine's own readiness check is asked for
    /// first and the document's load state is the fallback when the check cannot run.
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

    /// <summary>The guard report as the page serialises it.</summary>
    private sealed record GuardDto(
        string? Phase,
        int Pages,
        int Rows,
        int SplitRows,
        int MaxParts,
        int PartOverLimit,
        bool UnfinishedLastPage,
        string? AccountingError,
        string? Error);
}
