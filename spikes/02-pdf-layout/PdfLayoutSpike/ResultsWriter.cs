using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PdfLayoutSpike;

/// <summary>Environment facts recorded before and after the matrix.</summary>
internal sealed record RunEnvironment(
    string StartedAt,
    string EndedAt,
    int ProcessorCount,
    long? MemoryTotalKiB,
    string? CgroupMemoryMax,
    string? CgroupCpuMax,
    string OperatingSystem,
    string Runtime,
    string PlaywrightVersion,
    string ChromiumVersion,
    string? ChromiumCommandLine,
    string FontFamily,
    int TimeoutSeconds,
    string Matrix);

/// <summary>Writes summary.json (everything) and runs.csv (one row per run).</summary>
internal static class ResultsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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

    public static string OperatingSystemDescription => RuntimeInformation.OSDescription;

    public static async Task WriteAsync(string outDirectory, RunEnvironment environment, BlockSource source, SampleSummary? baseline, RunMatrix matrix, int exitCode)
    {
        var summary = new
        {
            environment,
            dataset = new { file = source.FileName, sha256 = source.Sha256, records = source.Records.Count },
            baseline,
            primary = new { configuration = matrix.Primary.Name, note = matrix.PrimaryNote, passesAccounting = matrix.PrimaryPassesAccounting },
            bestOtherConfiguration = matrix.BestOtherConfiguration,
            memorySeries = matrix.MemorySeries,
            displacement = matrix.PairChecks,
            determinism = matrix.DeterminismCheck,
            giantRowSignature = matrix.GiantRowSignatures,
            calibration = matrix.Calibrations,
            tallRows = matrix.TallRows,
            splitRowObservation = matrix.Runs
                .Where(r => r.Block.StartsWith('B') && r.Checks.Any(c => c.Name == AccountingVerifier.SplitRows))
                .Select(r => new { run = r.Id, observation = r.Checks.First(c => c.Name == AccountingVerifier.SplitRows).Detail }),
            skipped = matrix.Skipped,
            verdict = matrix.Verdict,
            runs = matrix.Runs,
            exitCode,
        };
        await using (var stream = File.Create(Path.Combine(outDirectory, "summary.json")))
        {
            await JsonSerializer.SerializeAsync(stream, summary, JsonOptions);
        }

        var csv = new StringBuilder();
        csv.Append("run_id,purpose,configuration,block,footer,repetition,status,pages,pdf_pages_heuristic,navigation_ms,layout_ms,paged_flow_ms,pdf_ms,pdf_bytes,")
            .Append("chromium_tree_peak_rss_kib,chromium_tree_peak_pss_kib,chromium_processes_max,driver_peak_rss_kib,harness_peak_rss_kib,harness_vmhwm_kib,")
            .Append("managed_heap_peak_bytes,tmp_disk_peak_bytes,cgroup_memory_peak_bytes,samples,checks_failed,outcome_class,k,tall_row_length,observed_span\n");
        foreach (var r in matrix.Runs)
        {
            var m = r.Memory;
            var failed = string.Join(' ', r.Checks.Where(c => !c.Pass).Select(c => c.Name));
            csv.AppendJoin(',', [
                r.Id, r.Purpose, r.Configuration, r.Block, r.Footer ? "on" : "off", N(r.Repetition), r.Status, N(r.Pages), N(r.PdfPagesHeuristic),
                N(r.NavigationMs), N(r.LayoutMs), N(r.PagedFlowMs), N(r.PdfMs), N(r.PdfBytes),
                N(m?.ChromiumTreePeakRssKiB), N(m?.ChromiumTreePeakPssKiB), N(m?.ChromiumProcessesMax), N(m?.DriverPeakRssKiB), N(m?.HarnessPeakRssKiB), N(r.HarnessHighWaterMarkKiB),
                N(m?.ManagedHeapPeakBytes), N(m?.TmpDiskPeakBytes), N(r.CgroupMemoryPeakBytes), N(m?.Samples), failed,
                r.OutcomeClass ?? string.Empty, r.TargetK?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, N(r.TallRowLength), N(r.ObservedSpan),
            ]).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(outDirectory, "runs.csv"), csv.ToString());
    }

    private static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string N(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string N(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;
}
