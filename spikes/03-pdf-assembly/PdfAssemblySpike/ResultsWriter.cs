using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PdfAssemblySpike;

/// <summary>One measured process: a whole variant run, or one of the two steps of the last variant.</summary>
internal sealed record StepRow(
    string Variant,
    string VariantName,
    int Size,
    int Parts,
    int Repetition,
    string Step,
    string Outcome,
    int Exit,
    double WallSeconds,
    double UserSeconds,
    double SystemSeconds,
    long PeakRssKb,
    long? VmHwmKb,
    long? ManagedHeapPeakBytes,
    long TempDiskPeakBytes,
    long OutputBytes,
    int? Gen0,
    int? Gen1,
    int? Gen2,
    string? Reason);

/// <summary>Median and maximum of one figure over the repetitions.</summary>
internal sealed record Figure(double? Median, double? Max);

/// <summary>Per size and variant: the figures over the successful repetitions.</summary>
internal sealed record CombinationSummary(
    string Variant,
    string VariantName,
    int Size,
    int Repetitions,
    int Successful,
    IReadOnlyList<string> Limits,
    Figure PeakRssMiB,
    Figure ManagedHeapPeakMiB,
    Figure WallSeconds,
    Figure TempDiskPeakMiB,
    Figure OutputMiB);

/// <summary>Writes runs.csv, summary.json and verification.json.</summary>
internal static class ResultsWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void WriteRuns(string path, IEnumerable<StepRow> rows)
    {
        var csv = new StringBuilder();
        csv.Append("variant,variantName,size,parts,repetition,step,outcome,exit,wallSeconds,userSeconds,systemSeconds,peakRssKb,vmHwmKb,managedHeapPeakBytes,tempDiskPeakBytes,outputBytes,gen0,gen1,gen2,reason\n");
        foreach (var r in rows)
        {
            csv.Append(string.Join(',',
                r.Variant, r.VariantName, Num(r.Size), Num(r.Parts), Num(r.Repetition), r.Step, r.Outcome, Num(r.Exit),
                r.WallSeconds.ToString("F2", Invariant), r.UserSeconds.ToString("F2", Invariant), r.SystemSeconds.ToString("F2", Invariant),
                Num(r.PeakRssKb), Num(r.VmHwmKb), Num(r.ManagedHeapPeakBytes), Num(r.TempDiskPeakBytes), Num(r.OutputBytes),
                Num(r.Gen0), Num(r.Gen1), Num(r.Gen2), Quote(r.Reason)));
            csv.Append('\n');
        }

        File.WriteAllText(path, csv.ToString());
    }

    public static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, SetManifest.Json) + "\n");

    public static Figure Of(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return new Figure(null, null);
        }

        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
        return new Figure(Math.Round(median, 2), Math.Round(sorted[^1], 2));
    }

    private static string Num(long? value) => value?.ToString(Invariant) ?? string.Empty;

    private static string Num(int? value) => value?.ToString(Invariant) ?? string.Empty;

    private static string Quote(string? value) =>
        value is null ? string.Empty : "\"" + value.Replace("\"", "'", StringComparison.Ordinal).Replace('\n', ' ') + "\"";
}
