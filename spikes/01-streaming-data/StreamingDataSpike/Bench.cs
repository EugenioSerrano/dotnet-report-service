using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using StreamingDataSpike.Measuring;
using StreamingDataSpike.Parsing;
using StreamingDataSpike.Rendering;

namespace StreamingDataSpike;

/// <summary>
/// The measurement protocol: every measured run in its own child process,
/// one discarded warm-up per input, then N runs; parse of volume 100 k / 500 k
/// / 1 M plain and gzip; render of volume 100 k and 1 M per engine (per-record
/// context) plus one per-block run per engine on 1 M. Writes the raw lines
/// and a summary with the flatness rule.
/// </summary>
internal static class Bench
{
    private const double MiB = 1_048_576;

    /// <summary>If one 1 M render run takes longer than this, that engine's remaining 1 M runs are skipped.</summary>
    private static readonly TimeSpan SlowRun = TimeSpan.FromMinutes(15);

    private static readonly string[] Sizes = ["100k", "500k", "1m"];

    private static readonly string[] Figures = ["managedHeapPeakBytes", "processPeakRssBytes"];

    public static async Task<int> RunAsync(Arguments arguments)
    {
        var datasets = arguments.Required("--datasets");
        var outputFolder = arguments.Required("--output");
        var runs = (int)arguments.Number("--runs", 3, 1);
        arguments.EnsureAllUsed(0);
        Directory.CreateDirectory(outputFolder);

        var parseRuns = new List<Dictionary<string, object?>>();
        var renderRuns = new List<Dictionary<string, object?>>();
        var notes = new List<string>();
        var failures = 0;

        using (var parseFile = OpenResults(outputFolder, "parse-runs.jsonl"))
        {
            foreach (var size in Sizes)
            {
                foreach (var format in new[] { "plain", "gzip" })
                {
                    var file = DatasetPath(datasets, size, format);
                    await ChildProcess.RunAsync(["parse", file]).ConfigureAwait(false);
                    for (var run = 1; run <= runs; run++)
                    {
                        var child = await ChildProcess.RunAsync(["parse", file]).ConfigureAwait(false);
                        var line = Tag(child.StandardOutput, "parse", size, format, null, null, run);
                        await parseFile.WriteLineAsync(line).ConfigureAwait(false);
                        var values = Values(child.StandardOutput);
                        values["size"] = size;
                        values["format"] = format;
                        parseRuns.Add(values);
                        if (child.ExitCode != 0 || values.GetValueOrDefault("status") as string != "ok")
                        {
                            failures++;
                        }

                        Console.Out.WriteLine($"parse {size} {format} run {run}: exit {child.ExitCode}");
                    }
                }
            }
        }

        using (var renderFile = OpenResults(outputFolder, "render-runs.jsonl"))
        {
            foreach (var size in new[] { "100k", "1m" })
            {
                foreach (var engine in Renderers.Engines)
                {
                    var file = DatasetPath(datasets, size, "plain");
                    string[] command = ["render", file, "--template", "pilot-volume", "--engine", engine, "--context", "per-record"];
                    await ChildProcess.RunAsync(command).ConfigureAwait(false);
                    for (var run = 1; run <= runs; run++)
                    {
                        var clock = Stopwatch.StartNew();
                        var child = await ChildProcess.RunAsync(command).ConfigureAwait(false);
                        await renderFile.WriteLineAsync(Tag(child.StandardOutput, "render", size, "plain", engine, "per-record", run)).ConfigureAwait(false);
                        var values = Values(child.StandardOutput);
                        values["size"] = size;
                        renderRuns.Add(values);
                        failures += child.ExitCode == 0 ? 0 : 1;
                        Console.Out.WriteLine($"render {size} {engine} per-record run {run}: exit {child.ExitCode}");
                        if (size == "1m" && clock.Elapsed > SlowRun && run < runs)
                        {
                            notes.Add($"{engine}: one 1 M render run took {clock.Elapsed.TotalMinutes:F1} min; its remaining 1 M runs were skipped.");
                            break;
                        }
                    }
                }
            }

            foreach (var engine in Renderers.Engines)
            {
                var file = DatasetPath(datasets, "1m", "plain");
                var child = await ChildProcess.RunAsync(["render", file, "--template", "pilot-volume", "--engine", engine, "--context", "per-block"]).ConfigureAwait(false);
                await renderFile.WriteLineAsync(Tag(child.StandardOutput, "render", "1m", "plain", engine, "per-block", 1)).ConfigureAwait(false);
                var values = Values(child.StandardOutput);
                values["size"] = "1m";
                renderRuns.Add(values);
                failures += child.ExitCode == 0 ? 0 : 1;
                Console.Out.WriteLine($"render 1m {engine} per-block: exit {child.ExitCode}");
            }
        }

        var summary = await SummaryAsync(datasets, parseRuns, renderRuns, notes).ConfigureAwait(false);
        await using (var writer = new StreamWriter(Path.Combine(outputFolder, "summary.md"), false, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(summary.ReplaceLineEndings("\n")).ConfigureAwait(false);
        }

        Console.Out.Write(summary.ReplaceLineEndings("\n"));
        return failures == 0 ? 0 : 1;
    }

    private static string DatasetPath(string folder, string size, string format) =>
        Path.Combine(folder, $"header-items-v1.volume.{size}.json" + (format == "gzip" ? ".gz" : string.Empty));

    private static StreamWriter OpenResults(string folder, string name) =>
        new(Path.Combine(folder, name), false, new UTF8Encoding(false)) { NewLine = "\n" };

    private static string Tag(string childLine, string phase, string size, string format, string? engine, string? context, int run)
    {
        var line = childLine.Trim();
        if (!line.StartsWith('{'))
        {
            line = "{\"status\":\"no-output\"}";
        }

        var prefix = new StringBuilder();
        prefix.Append(CultureInfo.InvariantCulture, $"{{\"phase\":\"{phase}\",\"size\":\"{size}\",\"format\":\"{format}\",");
        if (engine is not null)
        {
            prefix.Append(CultureInfo.InvariantCulture, $"\"engineRun\":\"{engine}\",\"contextRun\":\"{context}\",");
        }

        prefix.Append(CultureInfo.InvariantCulture, $"\"run\":{run},");
        return prefix + line[1..];
    }

    /// <summary>The top-level values of one result line.</summary>
    public static Dictionary<string, object?> Values(string childLine)
    {
        var line = childLine.Trim();
        if (!line.StartsWith('{'))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        return JsonValueModel.ReadObject(Encoding.UTF8.GetBytes(line), new JsonReaderOptions { MaxDepth = 16 }, 0, 0);
    }

    private static decimal Number(Dictionary<string, object?> values, string name) =>
        values.GetValueOrDefault(name) as decimal? ?? 0m;

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string Mib(decimal bytes) => ((double)bytes / MiB).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>Rule T: the larger size's median is at most 1.20 × the 100 k median + 8 MiB.</summary>
    public static bool RuleT(decimal median100k, decimal medianLarger) => medianLarger <= (1.20m * median100k) + 8_388_608m;

    private static async Task<string> SummaryAsync(string datasets, List<Dictionary<string, object?>> parseRuns, List<Dictionary<string, object?>> renderRuns, List<string> notes)
    {
        var text = new StringBuilder();
        text.AppendLine("# Measurement summary");
        text.AppendLine();
        text.AppendLine("Generated by `bench` from the raw lines in `parse-runs.jsonl` and `render-runs.jsonl`. Medians and maxima of the measured runs (warm-up runs discarded). MiB = 1 048 576 bytes. Chromium process tree: n/a (no browser runs). Temporary disk written by the measured commands: 0 bytes (hashing sinks).");
        text.AppendLine();

        text.AppendLine("## Datasets");
        text.AppendLine();
        text.AppendLine("| File | Bytes | SHA-256 |");
        text.AppendLine("|---|---|---|");
        foreach (var path in Directory.GetFiles(datasets, "header-items-v1.*").Order(StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| `{Path.GetFileName(path)}` | {new FileInfo(path).Length} | `{await Commands.Sha256Async(path).ConfigureAwait(false)}` |");
        }

        text.AppendLine();
        text.AppendLine("## Parse (volume dataset)");
        text.AppendLine();
        text.AppendLine("| Size | Format | Runs | Records | Managed heap peak MiB (median / max) | Process peak RSS MiB (median / max) | Chromium | Temp disk | Allocated MiB (median) | Gen 0/1/2 (median) | Seconds (median) | Records/s (median) | Max buffer bytes | Max record bytes |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var size in Sizes)
        {
            foreach (var format in new[] { "plain", "gzip" })
            {
                var runs = parseRuns.Where(run => (string?)run["size"] == size && (string?)run["format"] == format).ToList();
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {size} | {format} | {runs.Count} | {Median(runs.Select(r => Number(r, "records")))} | {Mib(Median(runs.Select(r => Number(r, "managedHeapPeakBytes"))))} / {Mib(runs.Max(r => Number(r, "managedHeapPeakBytes")))} | {Mib(Median(runs.Select(r => Number(r, "processPeakRssBytes"))))} / {Mib(runs.Max(r => Number(r, "processPeakRssBytes")))} | n/a | 0 | {Mib(Median(runs.Select(r => Number(r, "totalAllocatedBytes"))))} | {Median(runs.Select(r => Number(r, "gen0")))}/{Median(runs.Select(r => Number(r, "gen1")))}/{Median(runs.Select(r => Number(r, "gen2")))} | {Median(runs.Select(r => Number(r, "seconds"))):F2} | {Median(runs.Select(r => Number(r, "recordsPerSecond"))):F0} | {runs.Max(r => Number(r, "maxBufferBytes"))} | {runs.Max(r => Number(r, "maxRecordBytes"))} |");
            }
        }

        text.AppendLine();
        text.AppendLine("### Rule T (fixed before measuring)");
        text.AppendLine();
        text.AppendLine("`median(500k) ≤ 1.20 × median(100k) + 8 MiB` and `median(1M) ≤ 1.20 × median(100k) + 8 MiB`, for both figures and both formats.");
        text.AppendLine();
        text.AppendLine("| Format | Figure | Median 100k MiB | Median 500k MiB | Median 1M MiB | Bound MiB | Verdict |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        var allPass = true;
        foreach (var format in new[] { "plain", "gzip" })
        {
            foreach (var figure in Figures)
            {
                decimal MedianOf(string size) => Median(parseRuns.Where(run => (string?)run["size"] == size && (string?)run["format"] == format).Select(run => Number(run, figure)));
                var m100 = MedianOf("100k");
                var m500 = MedianOf("500k");
                var m1m = MedianOf("1m");
                var pass = RuleT(m100, m500) && RuleT(m100, m1m);
                allPass &= pass;
                text.AppendLine(CultureInfo.InvariantCulture, $"| {format} | `{figure}` | {Mib(m100)} | {Mib(m500)} | {Mib(m1m)} | {Mib((1.20m * m100) + 8_388_608m)} | {(pass ? "pass" : "FAIL")} |");
            }
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"**Rule T overall: {(allPass ? "pass" : "FAIL")}.**");
        text.AppendLine();

        text.AppendLine("## Render (volume dataset, template pilot-volume)");
        text.AppendLine();
        text.AppendLine("| Engine | Size | Context | Runs | Template parse ms (median) | Render µs/record (median) | Total seconds (median) | Managed heap peak MiB (median / max) | Process peak RSS MiB (median / max) | Allocated MiB (median) | HTML SHA-256 |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var engine in Renderers.Engines)
        {
            foreach (var (size, context) in new[] { ("100k", "per-record"), ("1m", "per-record"), ("1m", "per-block") })
            {
                var runs = renderRuns.Where(run => (string?)run["engine"] == engine && (string?)run["size"] == size && (string?)run["context"] == context).ToList();
                if (runs.Count == 0)
                {
                    continue;
                }

                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {engine} | {size} | {context} | {runs.Count} | {Median(runs.Select(r => Number(r, "templateParseMilliseconds"))):F1} | {Median(runs.Select(r => Number(r, "renderMicrosecondsPerRecord"))):F2} | {Median(runs.Select(r => Number(r, "seconds"))):F2} | {Mib(Median(runs.Select(r => Number(r, "managedHeapPeakBytes"))))} / {Mib(runs.Max(r => Number(r, "managedHeapPeakBytes")))} | {Mib(Median(runs.Select(r => Number(r, "processPeakRssBytes"))))} / {Mib(runs.Max(r => Number(r, "processPeakRssBytes")))} | {Mib(Median(runs.Select(r => Number(r, "totalAllocatedBytes"))))} | `{string.Join(", ", runs.Select(r => r.GetValueOrDefault("sha256") as string).Distinct())}` |");
            }
        }

        text.AppendLine();
        text.AppendLine("### Rule T applied to the render runs (informational: 1M against 100k)");
        text.AppendLine();
        text.AppendLine("| Engine | Figure | Median 100k MiB | Median 1M MiB | Bound MiB | Verdict |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var engine in Renderers.Engines)
        {
            foreach (var figure in Figures)
            {
                decimal MedianOf(string size) => Median(renderRuns.Where(run => (string?)run["engine"] == engine && (string?)run["size"] == size && (string?)run["context"] == "per-record").Select(run => Number(run, figure)));
                var m100 = MedianOf("100k");
                var m1m = MedianOf("1m");
                text.AppendLine(CultureInfo.InvariantCulture, $"| {engine} | `{figure}` | {Mib(m100)} | {Mib(m1m)} | {Mib((1.20m * m100) + 8_388_608m)} | {(RuleT(m100, m1m) ? "pass" : "fail")} |");
            }
        }

        if (notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Notes");
            text.AppendLine();
            foreach (var note in notes)
            {
                text.AppendLine("- " + note);
            }
        }

        return text.ToString();
    }
}
