using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PdfLayoutSpike;

/// <summary>What the in-page script reports once the layout is finished.</summary>
internal sealed class LayoutResult
{
    public int? Total { get; set; }

    public double? FlowMs { get; set; }

    public bool? FooterOn { get; set; }

    public bool? MarginBoxesFound { get; set; }

    public List<LayoutPage> Pages { get; set; } = [];

    public List<PageDiagnostic> Diagnostics { get; set; } = [];

    public int Scripts { get; set; }

    public string? AccountingError { get; set; }

    public string? Error { get; set; }
}

internal sealed class LayoutPage
{
    public int Page { get; set; }

    public List<LayoutFragment> Fragments { get; set; } = [];

    public FooterTexts Footer { get; set; } = new();

    public int Marks { get; set; }

    public double? AreaBottom { get; set; }

    public double? AreaTop { get; set; }

    /// <summary>Height of the page content area, in CSS pixels.</summary>
    public double? AreaHeight { get; set; }

    public List<int> BelowArea { get; set; } = [];

    public List<string> FooterOverflow { get; set; } = [];
}

internal sealed class LayoutFragment
{
    public int Seq { get; set; }

    public int Fragment { get; set; }

    public int Fragments { get; set; }

    public bool SplitFrom { get; set; }

    public bool SplitTo { get; set; }

    public string AmountBefore { get; set; } = string.Empty;

    public bool Mark { get; set; }

    public bool? Aligned { get; set; }

    /// <summary>Characters of the description shown in this fragment.</summary>
    public int DescChars { get; set; }

    public double? Top { get; set; }

    public double? Bottom { get; set; }
}

internal sealed class FooterTexts
{
    public string? Left { get; set; }

    public string? Center { get; set; }

    public string? Right { get; set; }
}

internal sealed class PageDiagnostic
{
    public int Page { get; set; }

    public int FragmentCount { get; set; }

    public bool FirstSplitFrom { get; set; }

    public bool LastSplitToAtLayout { get; set; }

    public bool HasBreakToken { get; set; }

    public bool? PreviousLastSplitToKnown { get; set; }
}

/// <summary>One laid-out and printed block, with its measurements.</summary>
internal sealed class RunResult
{
    public required string Id { get; init; }

    public required string Configuration { get; init; }

    public required string Block { get; init; }

    public required bool Footer { get; init; }

    public required int Repetition { get; init; }

    public required string Purpose { get; init; }

    public string Status { get; set; } = "ok";

    public string? Message { get; set; }

    public int? Pages { get; set; }

    public int? PdfPagesHeuristic { get; set; }

    public double NavigationMs { get; set; }

    public double? LayoutMs { get; set; }

    public double? PagedFlowMs { get; set; }

    public double? PdfMs { get; set; }

    public long? PdfBytes { get; set; }

    public int AbortedRequests { get; set; }

    public int NotFoundRequests { get; set; }

    public int Dialogs { get; set; }

    public int ScriptsExpected { get; set; }

    public int? ScriptsFound { get; set; }

    public SampleSummary? Memory { get; set; }

    public long? HarnessHighWaterMarkKiB { get; set; }

    public long? CgroupMemoryPeakBytes { get; set; }

    public List<CheckResult> Checks { get; set; } = [];

    public List<string> PageImages { get; set; } = [];

    /// <summary>Console warnings and errors of the page (at most 20).</summary>
    public List<string> ConsoleMessages { get; set; } = [];

    public string? PdfFile { get; set; }

    /// <summary>
    /// Outcome class of a characterisation run: "signature" or "different" for the
    /// giant-row fixture, "pass", "signature" or "other" for a tall-row fixture.
    /// </summary>
    public string? OutcomeClass { get; set; }

    /// <summary>Tall-row fixtures: the target multiple of the content-area height.</summary>
    public double? TargetK { get; set; }

    /// <summary>Tall-row fixtures: the actual length of the tall row's description.</summary>
    public int? TallRowLength { get; set; }

    /// <summary>Tall-row fixtures: the number of pages the tall row's fragments occupy.</summary>
    public int? ObservedSpan { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public LayoutResult? Layout { get; set; }
}

/// <summary>Loads each block into a fresh browser context, waits for the layout signal and prints it.</summary>
internal sealed partial class LayoutRunner : IAsyncDisposable
{
    private const string Origin = "http://layout.invalid";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IPlaywright playwright;
    private readonly IBrowser browser;
    private readonly string assetsRoot;
    private readonly ProcessTreeSampler sampler = new();

    private LayoutRunner(IPlaywright playwright, IBrowser browser, string assetsRoot)
    {
        this.playwright = playwright;
        this.browser = browser;
        this.assetsRoot = assetsRoot;
    }

    public string ChromiumVersion => browser.Version;

    public ProcessTreeSampler Sampler => sampler;

    /// <summary>
    /// The package version, read from the assembly's file version: the package's
    /// informational version attribute does not carry it (it reads "1.0.0+commit").
    /// </summary>
    public static string PlaywrightVersion =>
        typeof(IPlaywright).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? typeof(IPlaywright).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";

    /// <summary>Starts the driver and one headless browser (no channel: the installed headless shell).</summary>
    public static async Task<LayoutRunner> StartAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var assets = Path.Combine(AppContext.BaseDirectory, "assets");
        return new LayoutRunner(playwright, browser, Path.GetFullPath(assets));
    }

    /// <summary>Samples the idle browser (no page open) for the given time.</summary>
    public async Task<SampleSummary> SampleIdleAsync(TimeSpan duration)
    {
        sampler.Start();
        await Task.Delay(duration);
        return sampler.Stop();
    }

    public async Task<RunResult> RunAsync(RunResult run, string html, LayoutMode mode, string outDirectory, int timeoutSeconds, Func<LayoutResult, IEnumerable<int>>? imagePages = null)
    {
        var htmlBytes = Encoding.UTF8.GetBytes(html);
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var aborted = 0;
        var notFound = 0;
        var dialogs = 0;
        page.Dialog += async (_, dialog) =>
        {
            Interlocked.Increment(ref dialogs);
            await dialog.DismissAsync();
        };

        // The pagination library reports some layout failures only on the console
        // ("Unable to layout item", "Layout repeated") and still finishes; keep them.
        page.Console += (_, message) =>
        {
            if (message.Type is "warning" or "error")
            {
                lock (run.ConsoleMessages)
                {
                    if (run.ConsoleMessages.Count < 20)
                    {
                        run.ConsoleMessages.Add($"{message.Type}: {message.Text}");
                    }
                }
            }
        };
        await page.RouteAsync("**/*", async route =>
        {
            var served = Serve(route.Request.Url, htmlBytes);
            if (served is null)
            {
                Interlocked.Increment(ref aborted);
                await route.AbortAsync();
            }
            else if (served.Value.Body is null)
            {
                Interlocked.Increment(ref notFound);
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
            }
            else
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = served.Value.ContentType, BodyBytes = served.Value.Body });
            }
        });

        sampler.Start();
        var watch = Stopwatch.StartNew();
        try
        {
            await page.GotoAsync(Origin + "/block.html", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            run.NavigationMs = watch.Elapsed.TotalMilliseconds;

            if (mode == LayoutMode.Paged)
            {
                watch.Restart();
                await page.EvaluateAsync("() => { window.__startLayout(); }");
                try
                {
                    await page.WaitForFunctionAsync(
                        "() => document.documentElement.dataset.layout",
                        null,
                        new PageWaitForFunctionOptions { PollingInterval = 100, Timeout = timeoutSeconds * 1000f });
                }
                catch (TimeoutException e)
                {
                    run.Status = "layout-timeout";
                    run.Message = e.Message.Split('\n')[0];
                    run.Memory = sampler.Stop();
                    return run;
                }

                run.LayoutMs = watch.Elapsed.TotalMilliseconds;
                var signal = await page.EvaluateAsync<string>("() => document.documentElement.dataset.layout");
                var json = await page.EvaluateAsync<string>("() => JSON.stringify(window.__layoutResult)");
                run.Layout = JsonSerializer.Deserialize<LayoutResult>(json, JsonOptions);
                if (signal != "done")
                {
                    run.Status = "layout-failed";
                    run.Message = run.Layout?.Error ?? "layout failed without a message";
                    run.Memory = sampler.Stop();
                    return run;
                }

                run.Pages = run.Layout?.Total;
                run.PagedFlowMs = run.Layout?.FlowMs;
                run.ScriptsFound = run.Layout?.Scripts;
                await File.WriteAllTextAsync(Path.Combine(outDirectory, "layout", run.Id + ".json"), json);
            }
            else
            {
                run.ScriptsFound = await page.EvaluateAsync<int>("() => document.querySelectorAll('script').length");
            }

            var pdfPath = Path.Combine(outDirectory, "pdf", run.Id + ".pdf");
            watch.Restart();
            var pdf = await page.PdfAsync(new PagePdfOptions { PreferCSSPageSize = true, PrintBackground = true, Path = pdfPath });
            run.PdfMs = watch.Elapsed.TotalMilliseconds;
            run.Memory = sampler.Stop();
            run.PdfBytes = pdf.LongLength;
            run.PdfFile = "pdf/" + run.Id + ".pdf";
            run.PdfPagesHeuristic = CountPdfPages(pdf);
            if (mode == LayoutMode.Native)
            {
                run.Pages = run.PdfPagesHeuristic;
            }

            if (imagePages is not null && run.Layout is not null)
            {
                foreach (var number in imagePages(run.Layout).Distinct().Order())
                {
                    var name = $"png/{run.Id}-page{number:D3}.png";
                    await page.Locator(".pagedjs_page").Nth(number - 1).ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(outDirectory, name) });
                    run.PageImages.Add(name);
                }
            }
        }
        catch (PlaywrightException e)
        {
            run.Status = "error";
            run.Message = e.Message.Split('\n')[0];
            run.Memory ??= sampler.Stop();
        }
        finally
        {
            run.AbortedRequests = aborted;
            run.NotFoundRequests = notFound;
            run.Dialogs = dialogs;
            run.HarnessHighWaterMarkKiB = ProcessTreeSampler.HarnessHighWaterMarkKiB();
            run.CgroupMemoryPeakBytes = ProcessTreeSampler.CgroupMemoryPeakBytes();
        }

        return run;
    }

    /// <summary>Prints the native margin-box probe document to a PDF and returns its path and page count.</summary>
    public async Task<(string Path, int Pages)> ProbeNativeAsync(string html, string outDirectory)
    {
        var htmlBytes = Encoding.UTF8.GetBytes(html);
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.RouteAsync("**/*", async route =>
        {
            var served = Serve(route.Request.Url, htmlBytes);
            if (served?.Body is null)
            {
                await route.AbortAsync();
            }
            else
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = served.Value.ContentType, BodyBytes = served.Value.Body });
            }
        });
        await page.GotoAsync(Origin + "/block.html");
        var path = Path.Combine(outDirectory, "probe-native.pdf");
        var pdf = await page.PdfAsync(new PagePdfOptions { PreferCSSPageSize = true, PrintBackground = true, Path = path });
        return (path, CountPdfPages(pdf));
    }

    public async ValueTask DisposeAsync()
    {
        await browser.CloseAsync();
        playwright.Dispose();
    }

    /// <summary>
    /// Serves only the run's document and the packaged assets of the fake origin.
    /// Null: the request leaves the origin and is aborted. Null body: not found.
    /// </summary>
    private (string ContentType, byte[]? Body)? Serve(string url, byte[] html)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.Host != "layout.invalid")
        {
            return null;
        }

        if (uri.AbsolutePath == "/block.html")
        {
            return ("text/html; charset=utf-8", html);
        }

        const string prefix = "/assets/";
        if (uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            var relative = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
            var full = Path.GetFullPath(Path.Combine(assetsRoot, relative));
            if (full.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full))
            {
                var type = Path.GetExtension(full) switch
                {
                    ".js" => "text/javascript; charset=utf-8",
                    ".css" => "text/css; charset=utf-8",
                    _ => "application/octet-stream",
                };
                return (type, File.ReadAllBytes(full));
            }
        }

        return ("text/plain", null);
    }

    /// <summary>Heuristic page count: "/Type /Page" objects (not "/Pages") in the PDF bytes.</summary>
    private static int CountPdfPages(byte[] pdf) => PageObject().Count(Encoding.Latin1.GetString(pdf));

    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
    private static partial Regex PageObject();
}
