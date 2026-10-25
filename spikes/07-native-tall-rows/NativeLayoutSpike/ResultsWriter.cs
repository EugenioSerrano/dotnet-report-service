using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NativeLayoutSpike;

/// <summary>
/// Writes summary.json (environment and every run record) and runs.csv (one row
/// per run). Adapted from the earlier layout spike (spikes/02-pdf-layout, ResultsWriter.cs).
/// </summary>
internal static class ResultsWriter
{
    public static string? ReadCgroup(string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine("/sys/fs/cgroup", name)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task WriteAsync(string outDirectory, string matrix, DateTimeOffset startedAt, string fixtureSumsSha256, IReadOnlyList<RunRecord> runs)
    {
        var first = runs.FirstOrDefault(r => r.ChromiumVersion is not null);
        var summary = new
        {
            environment = new
            {
                startedAt = startedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                endedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                processorCount = Environment.ProcessorCount,
                memoryTotalKiB = ProcessTreeSampler.MemoryTotalKiB(),
                cgroupMemoryMax = ReadCgroup("memory.max"),
                cgroupMemorySwapMax = ReadCgroup("memory.swap.max"),
                cgroupCpuMax = ReadCgroup("cpu.max"),
                operatingSystem = RuntimeInformation.OSDescription,
                runtime = $".NET {Environment.Version}",
                playwright = LayoutRun.PlaywrightVersion,
                chromium = first?.ChromiumVersion,
                pdfBaseDigest = Environment.GetEnvironmentVariable("PDF_BASE_DIGEST"),
                fixtureSumsSha256,
                runCapSeconds = MatrixDriver.RunCap.TotalSeconds,
                killAfterSeconds = MatrixDriver.KillAfter.TotalSeconds,
                matrixBudgetHours = MatrixDriver.MatrixBudget.TotalHours,
                matrix,
            },
            outcomes = runs.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            runs,
        };
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(RunRecord.Json) { WriteIndented = true }) + "\n");

        var csv = new StringBuilder();
        csv.Append("run_id,group,series,fixture,markup,repetition,mutation,outcome,reason,pdf_pages_heuristic,pdf_bytes,html_bytes,")
            .Append("navigation_ms,fonts_ms,pdf_ms,total_ms,wall_ms,chromium_tree_peak_rss_kib,chromium_tree_peak_pss_kib,chromium_processes_max,")
            .Append("driver_peak_rss_kib,harness_peak_rss_kib,harness_vmhwm_kib,managed_heap_peak_bytes,tmp_disk_peak_bytes,samples,")
            .Append("dialogs,aborted_requests,scripts,oom_kill_before,oom_kill_after,leftover_processes_killed\n");
        foreach (var r in runs)
        {
            var m = r.Memory;
            csv.AppendJoin(',', [
                r.RunId, N(r.Group), r.Series ?? string.Empty, r.FixtureId, r.Markup, N(r.Repetition), r.Mutation ?? string.Empty, r.Outcome, Quote(r.Reason),
                N(r.PdfPagesHeuristic), N(r.PdfBytes), N(r.HtmlBytes), N(r.NavigationMs), N(r.FontsMs), N(r.PdfMs), N(r.TotalMs), N(r.WallMs),
                N(m?.ChromiumTreePeakRssKiB), N(m?.ChromiumTreePeakPssKiB), N(m?.ChromiumProcessesMax), N(m?.DriverPeakRssKiB), N(m?.HarnessPeakRssKiB),
                N(r.HarnessVmHwmKiB), N(m?.ManagedHeapPeakBytes), N(m?.TmpDiskPeakBytes), N(m?.Samples),
                N(r.Dialogs), N(r.AbortedRequests), N(r.Scripts), N(r.OomKillBefore), N(r.OomKillAfter), N(r.LeftoverProcessesKilled),
            ]).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(outDirectory, "runs.csv"), csv.ToString());
    }

    private static string Quote(string? value) => value is null ? string.Empty : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string N(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string N(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;
}
