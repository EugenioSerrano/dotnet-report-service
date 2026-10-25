using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TwoPassSpike;

/// <summary>
/// Writes the harness's summary.json (environment and every run record) and
/// runs.csv (one row per run, both passes). Copied from
/// spikes/07-native-tall-rows (ResultsWriter.cs) and changed for the two passes.
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

    public static async Task WriteAsync(string outDirectory, string matrix, DateTimeOffset startedAt, string fixtureSumsSha256, string fontFile, IReadOnlyList<TwoPassRecord> runs)
    {
        var first = runs.FirstOrDefault(r => r.Pass1.ChromiumVersion is not null);
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
                chromium = first?.Pass1.ChromiumVersion,
                pdfSharp = StampRun.PdfSharpVersion,
                pdfBaseDigest = Environment.GetEnvironmentVariable("PDF_BASE_DIGEST"),
                stampFontFile = fontFile,
                stampFontEncoding = Stamper.Encoding.ToString(),
                amountOffsetPt = Stamper.AmountOffsetPt,
                fixtureSumsSha256,
                passCapSeconds = MatrixDriver.PassCap.TotalSeconds,
                killAfterSeconds = MatrixDriver.KillAfter.TotalSeconds,
                matrixBudgetHours = MatrixDriver.MatrixBudget.TotalHours,
                matrix,
            },
            outcomes = new
            {
                pass1 = runs.GroupBy(r => r.Pass1.Outcome).ToDictionary(g => g.Key, g => g.Count()),
                pass2 = runs.GroupBy(r => r.Pass2.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            },
            runs,
        };
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(RunRecord.Json) { WriteIndented = true }) + "\n");

        var csv = new StringBuilder();
        csv.Append("run,group,series,fixture,markup,strategy,repetition,mutation,fault,")
            .Append("p1_outcome,p1_reason,p1_pages_heuristic,p1_pdf_bytes,p1_html_bytes,p1_navigation_ms,p1_fonts_ms,p1_pdf_ms,p1_total_ms,p1_wall_ms,")
            .Append("p1_chromium_tree_peak_rss_kib,p1_chromium_tree_peak_pss_kib,p1_chromium_processes_max,p1_driver_peak_rss_kib,p1_harness_peak_rss_kib,p1_harness_vmhwm_kib,")
            .Append("p1_managed_heap_peak_bytes,p1_tmp_disk_peak_bytes,p1_dialogs,p1_aborted_requests,p1_scripts,p1_oom_kill_before,p1_oom_kill_after,p1_leftover_processes_killed,")
            .Append("p2_outcome,p2_reason,p2_pages,p2_pdf_bytes,p2_open_ms,p2_read_ms,p2_compute_ms,p2_stamp_ms,p2_remove_ms,p2_save_ms,p2_total_ms,p2_wall_ms,")
            .Append("p2_annotations_read,p2_marker_annotations,p2_unknown_annotations,p2_markers_removed,p2_peak_rss_kib,p2_vmhwm_kib,p2_managed_heap_peak_bytes,p2_tmp_disk_peak_bytes,")
            .Append("p2_oom_kill_before,p2_oom_kill_after,p2_leftover_processes_killed\n");
        foreach (var r in runs)
        {
            var a = r.Pass1;
            var b = r.Pass2;
            csv.AppendJoin(',', [
                r.Run, N(r.Group), r.Series ?? string.Empty, r.FixtureId, r.Markup, r.Strategy, N(r.Repetition), r.Mutation ?? string.Empty, r.Fault ?? string.Empty,
                a.Outcome, Quote(a.Reason), N(a.Pages), N(a.PdfBytes), N(a.HtmlBytes), N(a.NavigationMs), N(a.FontsMs), N(a.PdfMs), N(a.TotalMs), N(a.WallMs),
                N(a.Memory?.ChromiumTreePeakRssKiB), N(a.Memory?.ChromiumTreePeakPssKiB), N(a.Memory?.ChromiumProcessesMax), N(a.Memory?.DriverPeakRssKiB), N(a.Memory?.HarnessPeakRssKiB), N(a.HarnessVmHwmKiB),
                N(a.Memory?.ManagedHeapPeakBytes), N(a.Memory?.TmpDiskPeakBytes), N(a.Dialogs), N(a.AbortedRequests), N(a.Scripts), N(a.OomKillBefore), N(a.OomKillAfter), N(a.LeftoverProcessesKilled),
                b.Outcome, Quote(b.Reason), N(b.Pages), N(b.PdfBytes), N(b.OpenMs), N(b.ReadMs), N(b.ComputeMs), N(b.StampMs), N(b.RemoveMs), N(b.SaveMs), N(b.TotalMs), N(b.WallMs),
                N(b.AnnotationsRead), N(b.MarkerAnnotations), N(b.UnknownAnnotations), N(b.MarkersRemoved), N(b.Memory?.HarnessPeakRssKiB), N(b.HarnessVmHwmKiB), N(b.Memory?.ManagedHeapPeakBytes), N(b.Memory?.TmpDiskPeakBytes),
                N(b.OomKillBefore), N(b.OomKillAfter), N(b.LeftoverProcessesKilled),
            ]).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(outDirectory, "runs.csv"), csv.ToString());
    }

    private static string Quote(string? value) => value is null ? string.Empty : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string N(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string N(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;
}
