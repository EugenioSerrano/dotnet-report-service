using System.Globalization;
using System.Text;

namespace MarkdownSpike.Measuring;

/// <summary>
/// Reads the CSV files and prints the comparison tables of the report: the
/// median of the runs per route and size, the spread, the flat-memory
/// classification and the continuity results.
/// </summary>
internal static class ResultSummary
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] Sizes = ["1k", "10k", "100k", "500k"];

    public static int Run(CommandLine line)
    {
        line.Allow("results");
        string results = line.Optional("results") ?? RepositoryPaths.Results;
        List<Dictionary<string, string>> measurements = Read(Path.Combine(results, "measurements.csv"));
        List<Dictionary<string, string>> continuity = Read(Path.Combine(results, "continuity.csv"));
        var report = new StringBuilder();

        report.Append("Memory (median of runs; spread = max - min) — standard mode, block size 48\n\n");
        report.Append("| Route | Size | Runs ok | Heap MiB | Heap spread | Working set MiB | WS spread | Elapsed s | Output MiB | Temp bytes |\n");
        report.Append("|---|---|---|---|---|---|---|---|---|---|\n");
        var medians = new Dictionary<(string Route, string Size), (double Heap, double Ws)>();
        foreach (string route in new[] { "html", "a", "b", "c", "d" })
        {
            foreach (string size in Sizes)
            {
                var rows = measurements.Where(r => r["route"] == route && r["mode"] == "standard" && SizeOf(r["dataset"]) == size).ToList();
                if (rows.Count == 0)
                {
                    continue;
                }

                var ok = rows.Where(r => r["outcome"] == "ok").ToList();
                if (ok.Count == 0)
                {
                    report.Append($"| {route} | {size} | 0/{rows.Count} | {rows[0]["outcome"]} {rows[0]["error"]} | | | | | | |\n");
                    continue;
                }

                List<double> heap = Numbers(ok, "peakManagedHeapMiB");
                List<double> ws = Numbers(ok, "peakWorkingSetMiB");
                medians[(route, size)] = (Median(heap), Median(ws));
                string branch = string.Join("/", ok.Select(r => r["branch"]).Where(b => b.Length > 0).Distinct());
                report.Append($"| {route}{(branch.Length > 0 ? " (" + branch + ")" : string.Empty)} | {size} | {ok.Count}/{rows.Count} | {Median(heap):0.0} | {heap.Max() - heap.Min():0.0} | {Median(ws):0.0} | {ws.Max() - ws.Min():0.0} | {Median(Numbers(ok, "elapsedSeconds")):0.0} | {Median(Numbers(ok, "outputBytes")) / 1048576:0.0} | {Numbers(ok, "tempDiskBytes").Max():0} |\n");
            }
        }

        report.Append("\nFlat-memory rule: flat when value(500k) <= 1.5 x value(10k) and value(500k) <= 1.25 x value(100k), for heap and working set\n\n");
        report.Append("| Route | Heap 10k / 100k / 500k | WS 10k / 100k / 500k | Classification |\n|---|---|---|---|\n");
        foreach (string route in new[] { "html", "a", "b", "c", "d" })
        {
            if (!medians.TryGetValue((route, "10k"), out var m10) || !medians.TryGetValue((route, "100k"), out var m100) || !medians.TryGetValue((route, "500k"), out var m500))
            {
                report.Append($"| {route} | — | — | not classified (missing sizes) |\n");
                continue;
            }

            bool heapFlat = m500.Heap <= 1.5 * m10.Heap && m500.Heap <= 1.25 * m100.Heap;
            bool wsFlat = m500.Ws <= 1.5 * m10.Ws && m500.Ws <= 1.25 * m100.Ws;
            string classification = heapFlat && wsFlat
                ? "flat"
                : $"grows: heap {(m500.Heap - m100.Heap) / 4:0.0} MiB / 100k records, WS {(m500.Ws - m100.Ws) / 4:0.0} MiB / 100k records{Extrapolate(m100, m500)}";
            report.Append($"| {route} | {m10.Heap:0.0} / {m100.Heap:0.0} / {m500.Heap:0.0} | {m10.Ws:0.0} / {m100.Ws:0.0} / {m500.Ws:0.0} | {classification} |\n");
        }

        report.Append("\nContinuity (every check run)\n\n");
        report.Append("| Route | Mode | Block size | Size | Outcomes | Tables | Max blocks spanned | Repeated headers | Heading / subtotal rows (per run) | Records missing | Unexpected rows | Cell mismatches | Raw HTML lines | Header fields |\n");
        report.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (IGrouping<(string, string, string, string), Dictionary<string, string>> group in continuity
            .GroupBy(r => (r["route"], r["mode"], r["blockSize"], SizeOf(r["dataset"])))
            .OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal).ThenBy(g => Array.IndexOf(Sizes, g.Key.Item4)))
        {
            (string route, string mode, string blockSize, string size) = group.Key;
            string outcomes = string.Join(", ", group.GroupBy(r => r["outcome"]).Select(o => $"{o.Count()}× {o.Key}"));
            Dictionary<string, string> first = group.First();
            report.Append($"| {route} | {mode} | {blockSize} | {size} | {outcomes} | {first["tables"]} | {first["maxBlocksSpanned"]} ({first["maxSpanGroup"]}) | {Sum(group, "repeatedHeaders")} | {first["headingRowsFound"]} / {first["subtotalRowsFound"]} | {Sum(group, "recordsMissing")} | {Sum(group, "unexpectedRows")} | {Sum(group, "cellCountMismatches")} | {Sum(group, "rawHtmlLines")} | {first["headerFieldsFound"]}/3 |\n");
        }

        report.Append("\nOther modes (median of runs)\n\n| Route | Mode | Size | Runs ok | Heap MiB | Working set MiB | Elapsed s |\n|---|---|---|---|---|---|---|\n");
        foreach (IGrouping<(string, string, string), Dictionary<string, string>> group in measurements
            .Where(r => r["mode"] != "standard")
            .GroupBy(r => (r["route"], r["mode"], SizeOf(r["dataset"]))))
        {
            var ok = group.Where(r => r["outcome"] == "ok").ToList();
            (string route, string mode, string size) = group.Key;
            report.Append(ok.Count == 0
                ? $"| {route} | {mode} | {size} | 0/{group.Count()} | | | |\n"
                : $"| {route} | {mode} | {size} | {ok.Count}/{group.Count()} | {Median(Numbers(ok, "peakManagedHeapMiB")):0.0} | {Median(Numbers(ok, "peakWorkingSetMiB")):0.0} | {Median(Numbers(ok, "elapsedSeconds")):0.0} |\n");
        }

        Console.Write(report.ToString());
        return 0;
    }

    private static string Extrapolate((double Heap, double Ws) at100, (double Heap, double Ws) at500)
    {
        double slope = (at500.Ws - at100.Ws) / 400_000.0;
        if (slope <= 0)
        {
            return string.Empty;
        }

        double records = 500_000 + ((1024 - at500.Ws) / slope);
        return records <= 500_000 ? "; WS already above 1 GiB" : $"; WS reaches 1 GiB at about {records / 1_000_000:0.0} million records (linear)";
    }

    private static string SizeOf(string dataset)
    {
        string[] parts = dataset.Split('.');
        return parts.Length >= 3 ? parts[^2] : dataset;
    }

    private static List<double> Numbers(IEnumerable<Dictionary<string, string>> rows, string column) =>
        [.. rows.Select(r => double.Parse(r[column], Invariant))];

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static long Sum(IEnumerable<Dictionary<string, string>> rows, string column) =>
        rows.Sum(r => long.TryParse(r[column], NumberStyles.Integer, Invariant, out long value) ? value : 0);

    private static List<Dictionary<string, string>> Read(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path))
        {
            return rows;
        }

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        List<string> header = CsvFile.Split(lines[0]);
        foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
        {
            List<string> cells = CsvFile.Split(line);
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < header.Count; i++)
            {
                row[header[i]] = i < cells.Count ? cells[i] : string.Empty;
            }

            rows.Add(row);
        }

        return rows;
    }
}
