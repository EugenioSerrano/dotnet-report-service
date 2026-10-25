using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PaginationFixtures;

namespace NativeLayoutSpike;

/// <summary>
/// One line of runs.jsonl: what was run, how it ended and what it measured.
/// The text-integrity checker reads only runId, fixtureId, markup, repetition,
/// mutation, marks, footer, outcome, reason, pdf and the escaping counters.
/// </summary>
internal sealed class RunRecord
{
    public required string RunId { get; set; }

    public required string FixtureId { get; set; }

    public required string Markup { get; set; }

    public int Repetition { get; set; } = 1;

    public string? Mutation { get; set; }

    public string Marks { get; set; } = "none";

    public string Footer { get; set; } = "none";

    public int Group { get; set; }

    public string? Series { get; set; }

    /// <summary>ok | timeout | killed-wall-clock | oom-killed | crashed | error | not-run.</summary>
    public string Outcome { get; set; } = "error";

    public string? Reason { get; set; }

    public string? Pdf { get; set; }

    public long? PdfBytes { get; set; }

    public int? PdfPagesHeuristic { get; set; }

    public string? StartedAt { get; set; }

    public double? WallMs { get; set; }

    public double? NavigationMs { get; set; }

    public double? FontsMs { get; set; }

    public double? PdfMs { get; set; }

    public double? TotalMs { get; set; }

    public int? Dialogs { get; set; }

    public int? AbortedRequests { get; set; }

    public int? Scripts { get; set; }

    public long? HtmlBytes { get; set; }

    public SampleSummary? Memory { get; set; }

    public long? HarnessVmHwmKiB { get; set; }

    public long? OomKillBefore { get; set; }

    public long? OomKillAfter { get; set; }

    public int? ChildExitCode { get; set; }

    public int? LeftoverProcessesKilled { get; set; }

    public string? ChromiumVersion { get; set; }

    public string? PlaywrightVersion { get; set; }

    public List<string> ConsoleMessages { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// One run in a fresh process: launch the base's headless shell (no channel),
/// serve the run's document from a fake origin and abort everything else, wait
/// for fonts, print with native layout and measure. Everything finishes within
/// the run cap or the run ends as "timeout". Route and isolation logic adapted
/// from the earlier layout spike (spikes/02-pdf-layout, LayoutRunner.cs).
/// </summary>
internal static partial class LayoutRun
{
    public const string Origin = "http://layout.invalid";

    /// <summary>The package version, read from the assembly's file version.</summary>
    public static string PlaywrightVersion =>
        typeof(IPlaywright).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? typeof(IPlaywright).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";

    public static async Task<RunRecord> ExecuteAsync(RunRecord run, Fixture fixture, HtmlBuilder builder, string outDirectory, TimeSpan cap)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan Remaining() => cap - watch.Elapsed;
        var printed = run.Mutation is null ? fixture : ControlMutations.Apply(fixture, run.Mutation);
        var html = Encoding.UTF8.GetBytes(builder.Build(printed, run.Markup));
        run.HtmlBytes = html.LongLength;
        run.PlaywrightVersion = PlaywrightVersion;
        var sampler = new ProcessTreeSampler();
        var aborted = 0;
        var dialogs = 0;
        var crashed = false;
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Timeout = Milliseconds(Remaining()) });
            run.ChromiumVersion = browser.Version;
            browser.Disconnected += (_, _) => crashed = true;
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(Milliseconds(Remaining()));
            page.SetDefaultNavigationTimeout(Milliseconds(Remaining()));
            page.Crash += (_, _) => crashed = true;
            page.Dialog += async (_, dialog) =>
            {
                Interlocked.Increment(ref dialogs);
                await dialog.DismissAsync();
            };
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
                if (IsDocument(route.Request.Url))
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html; charset=utf-8", BodyBytes = html });
                }
                else
                {
                    Interlocked.Increment(ref aborted);
                    await route.AbortAsync();
                }
            });

            sampler.Start();
            var step = Stopwatch.StartNew();
            await page.GotoAsync(Origin + "/block.html", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = Milliseconds(Remaining()) });
            run.NavigationMs = step.Elapsed.TotalMilliseconds;
            step.Restart();
            await page.WaitForFunctionAsync("() => document.fonts.status === 'loaded'", null, new PageWaitForFunctionOptions { PollingInterval = 50, Timeout = Milliseconds(Remaining()) });
            run.FontsMs = step.Elapsed.TotalMilliseconds;
            run.Scripts = await page.EvaluateAsync<int>("() => document.querySelectorAll('script').length");

            var pdfPath = Path.Combine(outDirectory, "pdf", run.RunId + ".pdf");
            step.Restart();
            var pdfTask = page.PdfAsync(new PagePdfOptions { PreferCSSPageSize = true, PrintBackground = true, Path = pdfPath });
            var remaining = Remaining();
            if (remaining <= TimeSpan.Zero || await Task.WhenAny(pdfTask, Task.Delay(remaining)) != pdfTask)
            {
                run.Memory = sampler.Stop();
                run.Outcome = "timeout";
                run.Reason = $"printing did not finish within the {cap.TotalSeconds:0} s run cap";
                return run;
            }

            var pdf = await pdfTask;
            run.PdfMs = step.Elapsed.TotalMilliseconds;
            run.Memory = sampler.Stop();
            run.PdfBytes = pdf.LongLength;
            run.Pdf = "pdf/" + run.RunId + ".pdf";
            run.PdfPagesHeuristic = PageObject().Count(Encoding.Latin1.GetString(pdf));
            run.Outcome = "ok";
        }
        catch (TimeoutException e)
        {
            run.Memory ??= sampler.Stop();
            run.Outcome = "timeout";
            run.Reason = FirstLine(e.Message);
        }
        catch (PlaywrightException e)
        {
            run.Memory ??= sampler.Stop();
            var message = FirstLine(e.Message);
            run.Outcome = crashed || message.Contains("crash", StringComparison.OrdinalIgnoreCase) || message.Contains("closed", StringComparison.OrdinalIgnoreCase) ? "crashed" : "error";
            run.Reason = message;
        }
        finally
        {
            run.Dialogs = dialogs;
            run.AbortedRequests = aborted;
            run.TotalMs = watch.Elapsed.TotalMilliseconds;
            run.HarnessVmHwmKiB = ProcessTreeSampler.HarnessHighWaterMarkKiB();
        }

        try
        {
            if (browser is not null)
            {
                await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            playwright?.Dispose();
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException)
        {
            // The driver kills any process left behind after the run.
        }

        return run;
    }

    /// <summary>Prints the versions of the browser automation package, the browser and the runtime.</summary>
    public static async Task<int> VersionsAsync()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        Console.WriteLine($"Playwright {PlaywrightVersion}");
        Console.WriteLine($"Chromium {browser.Version}");
        Console.WriteLine($".NET {Environment.Version}");
        return 0;
    }

    private static bool IsDocument(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "layout.invalid" && uri.AbsolutePath == "/block.html";

    private static float Milliseconds(TimeSpan span) => (float)Math.Max(1, span.TotalMilliseconds);

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
    private static partial Regex PageObject();
}
