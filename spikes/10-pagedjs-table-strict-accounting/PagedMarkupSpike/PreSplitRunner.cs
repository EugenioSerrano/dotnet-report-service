using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PaginationFixtures;
using PdfLayoutSpike;

namespace PreSplitLayoutSpike;

/// <summary>
/// One run in a fresh process: launch the base's headless shell (no channel),
/// serve the run's pages and the packaged assets from a fake origin and abort
/// everything else, load the PDF-path page, run the pre-split script, start the
/// pagination library (the unchanged accounting script of spikes/02-pdf-layout
/// starts it), wait for its layout-finished signal, print, then check the
/// semantic page in a fresh page and run the unchanged verifier. Everything
/// finishes within the run cap or the run ends as "timeout".
/// A copy of the runner of spikes/08-pagedjs-pre-split, changed only to take the
/// page builder of the run's markup, to name the markup in the verifier's
/// configuration, and to class a run as "crashed" only on the browser's own
/// crash or disconnection signals (every other exception is "error").
/// </summary>
internal static partial class PreSplitRunner
{
    public const string Origin = "http://layout.invalid";
    public const string LayoutRepeated = AccountingVerifier.LayoutRepeated;
    private const int ConsoleCap = 200;

    public static string PlaywrightVersion =>
        typeof(IPlaywright).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? typeof(IPlaywright).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";

    public static async Task<RunRecord> ExecuteAsync(RunRecord run, Fixture fixture, Func<Fixture, bool, (string Semantic, string Pdf)> build, string outDirectory, TimeSpan cap, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan Remaining() => cap - watch.Elapsed;
        var printed = run.Mutation is null ? fixture : ControlMutations.Apply(fixture, run.Mutation);
        var footerOn = run.Footer == "accounting";
        var (semanticHtml, pdfHtml) = build(printed, footerOn);
        var htmlDirectory = Path.Combine(outDirectory, "html");
        var semanticPath = Path.Combine(htmlDirectory, run.RunId + ".semantic.html");
        var pdfPagePath = Path.Combine(htmlDirectory, run.RunId + ".pdf.html");
        await File.WriteAllTextAsync(semanticPath, semanticHtml, cancellation);
        await File.WriteAllTextAsync(pdfPagePath, pdfHtml, cancellation);
        var sem = new SemResult { SemanticSha256Before = HashFile(semanticPath) };
        run.Sem = sem;
        run.HtmlBytes = new FileInfo(pdfPagePath).Length;
        run.PlaywrightVersion = PlaywrightVersion;
        run.ScriptsExpected = PageBuilder.PdfScripts;
        var assetsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "assets"));
        byte[]? servedPdfPage = null;
        var sampler = new ProcessTreeSampler();
        var aborted = 0;
        var notFound = 0;
        var dialogs = 0;
        var crashed = false;
        var verifierConsole = new List<string>();
        LayoutResult? layout = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;

        void Capture(string level, string text)
        {
            lock (run.Console)
            {
                run.ConsoleTotal++;
                if (run.Console.Count < ConsoleCap)
                {
                    run.Console.Add(new ConsoleEntry(level, text));
                }
                else
                {
                    run.ConsoleDropped++;
                }

                var repeated = text.Contains(LayoutRepeated, StringComparison.Ordinal);
                if (repeated && run.LayoutRepeatedOccurrences.Count < 50)
                {
                    run.LayoutRepeatedOccurrences.Add(new ConsoleEntry(level, text));
                }

                // The verifier's own list: warnings and errors as "level: text", as spikes/02-pdf-layout captured them;
                // a "Layout repeated" message is always kept so the cap cannot hide it.
                if ((level is "warning" or "error") && (verifierConsole.Count < ConsoleCap || repeated))
                {
                    verifierConsole.Add($"{level}: {text}");
                }
            }
        }

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
            page.Console += (_, message) => Capture(message.Type, message.Text);
            page.PageError += (_, error) => Capture("pageerror", error);
            await context.RouteAsync("**/*", async route =>
            {
                var served = Serve(route.Request.Url, assetsRoot, semanticPath, pdfPagePath);
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
                    if (served.Value.IsPdfPage)
                    {
                        servedPdfPage = served.Value.Body;
                    }

                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = served.Value.ContentType, BodyBytes = served.Value.Body });
                }
            });

            sampler.Start();
            var step = Stopwatch.StartNew();
            await page.GotoAsync(Origin + "/pdf.html", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = Milliseconds(Remaining()) });
            run.NavigationMs = step.Elapsed.TotalMilliseconds;
            step.Restart();
            await page.EvaluateAsync("async () => { await document.fonts.ready; }");
            run.FontsMs = step.Elapsed.TotalMilliseconds;

            if (run.Split != Plans.Off)
            {
                var options = new Dictionary<string, object?>
                {
                    ["mode"] = run.Split,
                    ["trigger"] = run.Trigger ?? Plans.DefaultTrigger,
                    ["partLimit"] = run.PartLimit ?? Plans.DefaultPartLimit,
                };
                var splitTask = page.EvaluateAsync("o => window.__preSplit(o)", options);
                if (!await Within(splitTask, Remaining()))
                {
                    return TimedOut(run, sampler, cap, "the pre-split did not finish");
                }

                try
                {
                    await splitTask;
                }
                catch (PlaywrightException e) when (!crashed)
                {
                    run.SplitResult = await ReadJsonAsync(page, "window.__splitResult");
                    throw new InvalidOperationException("The pre-split script failed: " + FirstLine(e.Message), e);
                }

                run.SplitResult = await ReadJsonAsync(page, "window.__splitResult");
                run.MeasureMs = run.SplitResult?["measureMs"]?.GetValue<double>();
                run.SplitMs = run.SplitResult?["splitMs"]?.GetValue<double>();
            }

            step.Restart();
            await page.EvaluateAsync("() => { window.__startLayout(); }");
            var waitTask = page.WaitForFunctionAsync("() => document.documentElement.dataset.layout", null, new PageWaitForFunctionOptions { PollingInterval = 100, Timeout = Milliseconds(Remaining()) });
            if (!await Within(waitTask, Remaining()))
            {
                return TimedOut(run, sampler, cap, "the layout did not finish");
            }

            await waitTask;
            run.LayoutMs = step.Elapsed.TotalMilliseconds;
            var signal = await page.EvaluateAsync<string>("() => document.documentElement.dataset.layout");
            var layoutJson = await page.EvaluateAsync<string>("() => JSON.stringify(window.__layoutResult)");
            layout = JsonSerializer.Deserialize<LayoutResult>(layoutJson, RunRecord.Json);
            Directory.CreateDirectory(Path.Combine(outDirectory, "layout"));
            await File.WriteAllTextAsync(Path.Combine(outDirectory, "layout", run.RunId + ".json"), layoutJson, cancellation);
            run.LayoutFile = "layout/" + run.RunId + ".json";
            run.Pages = layout?.Total;
            run.PagedFlowMs = layout?.FlowMs;
            run.ScriptsFound = layout?.Scripts;
            if (signal != "done")
            {
                run.Memory = sampler.Stop();
                run.Outcome = "crashed";
                run.Reason = "the page's layout failed: " + FirstLine(layout?.Error ?? "no message");
                return run;
            }

            var pdfPath = Path.Combine(outDirectory, "pdf", run.RunId + ".pdf");
            step.Restart();
            var pdfTask = page.PdfAsync(new PagePdfOptions { PreferCSSPageSize = true, PrintBackground = true, Path = pdfPath });
            if (!await Within(pdfTask, Remaining()))
            {
                return TimedOut(run, sampler, cap, "printing did not finish");
            }

            var pdf = await pdfTask;
            run.PdfMs = step.Elapsed.TotalMilliseconds;
            run.Memory = sampler.Stop();
            run.PdfBytes = pdf.LongLength;
            run.Pdf = "pdf/" + run.RunId + ".pdf";
            run.PdfPagesHeuristic = PageObject().Count(Encoding.Latin1.GetString(pdf));
            run.Outcome = "ok";

            // SEM (c): a fresh page loads the semantic source; every item row once, unsplit, with the source text.
            await CheckSemanticPageAsync(context, printed, sem, Remaining());
        }
        catch (TimeoutException e)
        {
            run.Memory ??= sampler.Stop();
            if (run.Outcome != "ok")
            {
                run.Outcome = "timeout";
                run.Reason = FirstLine(e.Message);
            }
            else
            {
                sem.C = false;
                sem.Detail = "the semantic page check did not finish within the run cap: " + FirstLine(e.Message);
            }
        }
        catch (PlaywrightException e)
        {
            run.Memory ??= sampler.Stop();
            var message = FirstLine(e.Message);
            if (run.Outcome != "ok")
            {
                run.Outcome = crashed ? "crashed" : "error";
                run.Reason = message;
            }
            else
            {
                sem.C = false;
                sem.Detail = "the semantic page check failed: " + message;
            }
        }
        finally
        {
            run.Dialogs = dialogs;
            run.AbortedRequests = aborted;
            run.NotFoundRequests = notFound;
            run.TotalMs = watch.Elapsed.TotalMilliseconds;
            run.HarnessVmHwmKiB = ProcessTreeSampler.HarnessHighWaterMarkKiB();
            run.CgroupMemoryPeakBytes = ProcessTreeSampler.CgroupMemoryPeakBytes();
            run.Scripts = run.ScriptsFound is { } found ? Math.Max(0, found - PageBuilder.PdfScripts) : null;
            lock (run.Console)
            {
                run.LayoutRepeatedAnyLevel = run.LayoutRepeatedOccurrences.Count > 0;
            }
        }

        // SEM (a) and (b).
        sem.SemanticSha256After = HashFile(semanticPath);
        sem.A = sem.SemanticSha256Before == sem.SemanticSha256After;
        var semanticInner = PageBuilder.BodyInner(await File.ReadAllBytesAsync(semanticPath, cancellation));
        var pdfInner = servedPdfPage is null ? null : PageBuilder.BodyInner(servedPdfPage);
        sem.SemanticInnerSha256 = semanticInner is null ? null : Convert.ToHexStringLower(SHA256.HashData(semanticInner.Value.Inner));
        sem.PdfInnerSha256 = pdfInner is null ? null : Convert.ToHexStringLower(SHA256.HashData(pdfInner.Value.Inner));
        sem.SemanticBodyTag = semanticInner?.StartTag;
        sem.PdfBodyTag = pdfInner?.StartTag;
        sem.B = semanticInner is not null && pdfInner is not null && semanticInner.Value.Inner.AsSpan().SequenceEqual(pdfInner.Value.Inner)
            && semanticInner.Value.StartTag == "<body>" && pdfInner.Value.StartTag == $"<body data-footer=\"{(footerOn ? "on" : "off")}\">";

        // The unchanged verifier of spikes/02-pdf-layout.
        if (layout is not null && run.Outcome == "ok")
        {
            var block = FixtureAdapter.ToBlock(printed);
            var result = new RunResult
            {
                Id = run.RunId,
                Configuration = $"paged-{run.Markup}-keep-presplit",
                Block = fixture.Id,
                Footer = footerOn,
                Repetition = run.Repetition,
                Purpose = "pre-split",
                Status = "ok",
                Pages = layout.Total,
                Dialogs = run.Dialogs ?? 0,
                AbortedRequests = run.AbortedRequests ?? 0,
                NotFoundRequests = run.NotFoundRequests ?? 0,
                ScriptsExpected = PageBuilder.PdfScripts,
                ScriptsFound = run.ScriptsFound,
                Layout = layout,
            };
            result.ConsoleMessages.AddRange(verifierConsole);
            run.Rules = AccountingVerifier.VerifyPaged(block, result);
            result.Checks = run.Rules;
            run.LayoutRepeatedError = AccountingVerifier.LogsLayoutRepeated(result);
            if (fixture.Id == "G200" || (fixture.Id.StartsWith('T') && run.Mutation is null))
            {
                run.Signature = AccountingVerifier.EvaluateSignature(block, result, FixtureSeries.TallRowSequence);
            }
        }
        else
        {
            run.LayoutRepeatedError = verifierConsole.Any(m => m.StartsWith("error:", StringComparison.Ordinal) && m.Contains(LayoutRepeated, StringComparison.Ordinal));
        }

        try
        {
            if (browser is not null)
            {
                await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellation);
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
        System.Console.WriteLine($"Playwright {PlaywrightVersion}");
        System.Console.WriteLine($"Chromium {browser.Version}");
        System.Console.WriteLine($".NET {Environment.Version}");
        return 0;
    }

    private static async Task CheckSemanticPageAsync(IBrowserContext context, Fixture printed, SemResult sem, TimeSpan remaining)
    {
        var page = await context.NewPageAsync();
        try
        {
            page.SetDefaultTimeout(Milliseconds(remaining));
            await page.GotoAsync(Origin + "/semantic.html", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = Milliseconds(remaining) });
            var json = await page.EvaluateAsync<string>(
                "() => JSON.stringify({ scripts: document.scripts.length, rows: Array.from(document.querySelectorAll('.item')).map(r => ({ "
                + "seq: r.getAttribute('data-seq'), part: r.classList.contains('part') || r.hasAttribute('data-part'), "
                + "desc: r.querySelector(':scope > .desc') ? r.querySelector(':scope > .desc').textContent : null, "
                + "note: r.querySelector(':scope > .note') ? r.querySelector(':scope > .note').textContent : null })) })");
            var node = JsonNode.Parse(json)!;
            var rows = node["rows"]!.AsArray();
            var items = printed.Items.ToList();
            sem.ScriptsInSemanticPage = node["scripts"]!.GetValue<int>();
            sem.RowsExpected = items.Count;
            sem.RowsFound = rows.Count;
            sem.RowsUnsplit = rows.Count(r => r!["part"]!.GetValue<bool>() == false);
            var equal = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var seq = rows[i]!["seq"]?.GetValue<string>() ?? string.Empty;
                if (!seen.Add(seq))
                {
                    duplicates++;
                }

                if (i < items.Count
                    && seq == items[i].Sequence!.Value.ToString(CultureInfo.InvariantCulture)
                    && rows[i]!["desc"]?.GetValue<string>() == items[i].Description
                    && (!printed.HasNote || rows[i]!["note"]?.GetValue<string>() == (items[i].Note ?? string.Empty)))
                {
                    equal++;
                }
            }

            sem.TextsEqual = equal;
            sem.C = sem.ScriptsInSemanticPage == 0 && rows.Count == items.Count && sem.RowsUnsplit == items.Count && duplicates == 0 && equal == items.Count;
            if (sem.C != true)
            {
                sem.Detail = $"scripts {sem.ScriptsInSemanticPage}, rows {rows.Count}/{items.Count}, unsplit {sem.RowsUnsplit}, duplicated {duplicates}, texts equal {equal}";
            }
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static RunRecord TimedOut(RunRecord run, ProcessTreeSampler sampler, TimeSpan cap, string what)
    {
        run.Memory ??= sampler.Stop();
        run.Outcome = "timeout";
        run.Reason = $"{what} within the {cap.TotalSeconds:0} s run cap";
        run.Scripts = null;
        return run;
    }

    private static async Task<bool> Within(Task task, TimeSpan remaining) =>
        remaining > TimeSpan.Zero && await Task.WhenAny(task, Task.Delay(remaining)) == task;

    private static async Task<JsonNode?> ReadJsonAsync(IPage page, string expression)
    {
        var json = await page.EvaluateAsync<string?>($"() => {expression} === undefined ? null : JSON.stringify({expression})");
        return json is null ? null : JsonNode.Parse(json);
    }

    /// <summary>
    /// Serves the run's two pages and the packaged assets of the fake origin.
    /// Null: the request leaves the origin and is aborted. Null body: not found.
    /// </summary>
    private static (string ContentType, byte[]? Body, bool IsPdfPage)? Serve(string url, string assetsRoot, string semanticPath, string pdfPagePath)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.Host != "layout.invalid")
        {
            return null;
        }

        if (uri.AbsolutePath == "/pdf.html")
        {
            return ("text/html; charset=utf-8", File.ReadAllBytes(pdfPagePath), true);
        }

        if (uri.AbsolutePath == "/semantic.html")
        {
            return ("text/html; charset=utf-8", File.ReadAllBytes(semanticPath), false);
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
                return (type, File.ReadAllBytes(full), false);
            }
        }

        return ("text/plain", null, false);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static float Milliseconds(TimeSpan span) => (float)Math.Max(1, span.TotalMilliseconds);

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
    private static partial Regex PageObject();
}
