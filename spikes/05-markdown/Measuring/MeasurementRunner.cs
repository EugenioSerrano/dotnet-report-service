using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarkdownSpike.Checking;
using MarkdownSpike.Routes;

namespace MarkdownSpike.Measuring;

/// <summary>
/// Runs every size, route and run in a fresh child process, one at a time,
/// checks each output in another child process, and writes the CSV files.
/// </summary>
internal static class MeasurementRunner
{
    private static readonly string[] MeasurementColumns =
    [
        "date", "referenceEnvironmentRevision", "dataset", "sha256", "records", "groups", "blockSize", "route", "branch", "mode", "run",
        "outcome", "elapsedSeconds", "outputBytes", "peakManagedHeapMiB", "peakWorkingSetMiB", "tempDiskBytes", "gen0", "gen1", "gen2", "error",
    ];

    private static readonly string[] ContinuityColumns =
    [
        "date", "dataset", "route", "branch", "mode", "blockSize", "run", "outcome", "tables", "expectedTables", "groups", "maxBlocksSpanned",
        "maxSpanGroup", "separatorsPerTableMax", "repeatedHeaders", "rowsChecked", "headingRowsFound", "subtotalRowsFound", "recordsMissing", "unexpectedRows", "cellCountMismatches",
        "rawHtmlLines", "headerFieldsFound", "firstFailures",
    ];

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static int Run(CommandLine line)
    {
        line.Allow("data-dir", "work", "sizes", "runs", "routes", "block-size", "timeout-minutes", "results", "no-extras");
        string dataDir = line.Required("data-dir");
        string work = line.Required("work");
        string[] sizes = (line.Optional("sizes") ?? "1k,10k,100k,500k").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] routes = (line.Optional("routes") ?? "html,a,b,c,d").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int runs = line.Int("runs", 3);
        int blockSize = line.Int("block-size", ConvertCommand.DefaultBlockSize);
        var timeout = TimeSpan.FromMinutes(line.Int("timeout-minutes", 20));
        string results = line.Optional("results") ?? RepositoryPaths.Results;
        bool extras = !line.Flag("no-extras");

        Directory.CreateDirectory(work);
        Directory.CreateDirectory(results);
        var runner = new Runner(work, timeout, ReferenceEnvironmentRevision(),
            new CsvFile(Path.Combine(results, "measurements.csv"), MeasurementColumns),
            new CsvFile(Path.Combine(results, "continuity.csv"), ContinuityColumns));

        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string size in sizes)
        {
            string data = Path.Combine(dataDir, $"header-items-v1.grouped.{size}.json");
            Dataset dataset = Dataset.Open(data);
            foreach (string route in routes)
            {
                for (int run = 1; run <= runs; run++)
                {
                    if (skipped.TryGetValue(route, out string? reason))
                    {
                        runner.RecordSkipped(dataset, route, blockSize, "standard", run, reason);
                        continue;
                    }

                    (string outcome, string? check) = runner.Measure(dataset, route, blockSize, "standard", run, []);
                    if (outcome != "ok" && SizeIndex(size) >= SizeIndex("100k"))
                    {
                        skipped[route] = $"skipped: {outcome} at {size}";
                    }
                    else if (check == "broken" && SizeIndex(size) >= SizeIndex("10k"))
                    {
                        skipped[route] = $"skipped: tables broken at {size}";
                    }
                }
            }

            if (extras && size is "1k" or "10k")
            {
                for (int run = 1; run <= runs; run++)
                {
                    runner.Measure(dataset, "d", blockSize, "force-whole", run, ["--force-whole"]);
                }
            }

            if (extras && size == "10k")
            {
                foreach (string route in new[] { "a", "b", "c" })
                {
                    runner.Measure(dataset, route, 1000, "block-1000", 1, []);
                }
            }
        }

        runner.Close();
        Console.WriteLine($"measure: {runner.Children} child runs; results in {results}");
        return 0;
    }

    private static int SizeIndex(string size) => size switch
    {
        "1k" => 1,
        "10k" => 2,
        "100k" => 3,
        "500k" => 4,
        _ => 5,
    };

    /// <summary>The page revision of the reference environment, and its last commit when the page is committed.</summary>
    private static string ReferenceEnvironmentRevision()
    {
        string page = Path.Combine(RepositoryPaths.Root, "docs", "reference-environment.md");
        string revision = "not found";
        if (File.Exists(page))
        {
            string? revisionLine = File.ReadLines(page).FirstOrDefault(l => l.StartsWith("**Page revision:**", StringComparison.Ordinal));
            if (revisionLine is null)
            {
                revision = "unknown";
            }
            else
            {
                string text = revisionLine["**Page revision:**".Length..].Trim();
                int end = text.IndexOf(". ", StringComparison.Ordinal);
                revision = end < 0 ? text.TrimEnd('.') : text[..end];
            }
        }

        string commit = Git("log", "-1", "--format=%h", "--", "docs/reference-environment.md");
        return revision + (commit.Length > 0 ? $" @{commit}" : " (page not committed)");
    }

    private static string Git(params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = RepositoryPaths.Root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private sealed record Dataset(string Path, string Sha256)
    {
        public static Dataset Open(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return new Dataset(path, Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
    }

    private sealed class Runner(string work, TimeSpan timeout, string referenceRevision, CsvFile measurements, CsvFile continuity)
    {
        private readonly string program = typeof(MeasurementRunner).Assembly.Location;

        public int Children { get; private set; }

        public (string Outcome, string? Check) Measure(Dataset dataset, string route, int blockSize, string mode, int run, string[] extra)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(dataset.Path);
            string id = $"{route}-{name}-{mode}-{run}-{Guid.NewGuid():N}";
            string output = System.IO.Path.Combine(work, id + (route == "html" ? ".html" : ".md"));
            string temp = System.IO.Path.Combine(work, "tmp-" + id);
            Directory.CreateDirectory(temp);
            try
            {
                List<string> args = ["convert", "--route", route, "--data", dataset.Path, "--output", output, "--block-size", blockSize.ToString(Invariant), "--temp", temp, .. extra];
                (bool finished, string? json, string error) = RunChild(args);
                ConvertResult? result = json is null ? null : JsonSerializer.Deserialize<ConvertResult>(json, ResultJson.Options);
                string outcome = !finished ? "did-not-finish" : result?.Outcome ?? "failed";
                string message = !finished ? $"killed after {timeout.TotalMinutes} min" : result?.Error ?? (result is null ? Trim(error) : string.Empty);
                measurements.Write(
                [
                    Now(), referenceRevision, System.IO.Path.GetFileName(dataset.Path), dataset.Sha256, Text(result?.Records), Text(result?.Groups), blockSize.ToString(Invariant),
                    route, result?.Branch ?? string.Empty, mode, run.ToString(Invariant), outcome, Text(result?.ElapsedSeconds), Text(result?.OutputBytes),
                    Mib(result?.PeakManagedHeapBytes), Mib(result?.PeakWorkingSetBytes), Text(result?.TempDiskBytes), Text(result?.Gen0), Text(result?.Gen1), Text(result?.Gen2), message,
                ]);
                Console.WriteLine($"{route,-4} {name,-34} {mode,-11} run {run}: {outcome} {result?.ElapsedSeconds:0.0}s heap {Mib(result?.PeakManagedHeapBytes)} MiB ws {Mib(result?.PeakWorkingSetBytes)} MiB");

                string? check = null;
                if (route != "html" && outcome == "ok")
                {
                    (bool checkFinished, string? checkJson, string checkError) = RunChild(["check", "--data", dataset.Path, "--output", output, "--block-size", blockSize.ToString(Invariant)]);
                    CheckResult? c = checkJson is null ? null : JsonSerializer.Deserialize<CheckResult>(checkJson, ResultJson.Options);
                    check = c?.Outcome ?? (checkFinished ? "check-failed" : "check-did-not-finish");
                    continuity.Write(
                    [
                        Now(), System.IO.Path.GetFileName(dataset.Path), route, result?.Branch ?? string.Empty, mode, blockSize.ToString(Invariant), run.ToString(Invariant), check,
                        Text(c?.Tables), Text(c?.ExpectedTables), Text(c?.Groups), Text(c?.MaxBlocksSpanned), c?.MaxSpanGroup ?? string.Empty, Text(c?.SeparatorsPerTableMax),
                        Text(c?.RepeatedHeaders), Text(c?.RowsChecked), Text(c?.HeadingRowsFound), Text(c?.SubtotalRowsFound), Text(c?.RecordsMissing), Text(c?.UnexpectedRows), Text(c?.CellCountMismatches), Text(c?.RawHtmlLines),
                        Text(c?.HeaderFieldsFound), c is null ? Trim(checkError) : string.Join(" / ", c.Failures.Take(3)),
                    ]);
                    Console.WriteLine($"     check: {check} tables={c?.Tables} maxBlocksSpanned={c?.MaxBlocksSpanned} rows={c?.RowsChecked} missing={c?.RecordsMissing} unexpected={c?.UnexpectedRows} rawHtml={c?.RawHtmlLines}");
                }

                return (outcome, check);
            }
            finally
            {
                File.Delete(output);
                Directory.Delete(temp, recursive: true);
            }
        }

        public void RecordSkipped(Dataset dataset, string route, int blockSize, string mode, int run, string reason)
        {
            measurements.Write(
            [
                Now(), referenceRevision, System.IO.Path.GetFileName(dataset.Path), dataset.Sha256, string.Empty, string.Empty, blockSize.ToString(Invariant),
                route, string.Empty, mode, run.ToString(Invariant), "skipped", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, reason,
            ]);
            Console.WriteLine($"{route,-4} {System.IO.Path.GetFileName(dataset.Path),-34} {mode,-11} run {run}: {reason}");
        }

        public void Close()
        {
            measurements.Dispose();
            continuity.Dispose();
        }

        private (bool Finished, string? Json, string Error) RunChild(List<string> args)
        {
            Children++;
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add(program);
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return (false, null, string.Empty);
            }

            string json = stdout.Result.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{')) ?? string.Empty;
            return (true, json.Length == 0 ? null : json, stderr.Result);
        }

        private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", Invariant);

        private static string Text(long? value) => value?.ToString(Invariant) ?? string.Empty;

        private static string Text(double? value) => value?.ToString("0.000", Invariant) ?? string.Empty;

        private static string Mib(long? bytes) => bytes is null ? string.Empty : (bytes.Value / 1048576.0).ToString("0.0", Invariant);

        private static string Trim(string text) => text.Length > 300 ? text[..300] : text.Trim();
    }
}

/// <summary>A small CSV writer (RFC 4180 quoting, LF line ends, UTF-8 without byte-order mark).</summary>
internal sealed class CsvFile : IDisposable
{
    private readonly StreamWriter writer;

    public CsvFile(string path, string[] columns)
    {
        writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        Write(columns);
    }

    public void Write(IEnumerable<string> cells) =>
        writer.WriteLine(string.Join(",", cells.Select(Quote)));

    public void Dispose() => writer.Dispose();

    public static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    private static string Quote(string cell) =>
        cell.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ') + "\"" : cell;
}
