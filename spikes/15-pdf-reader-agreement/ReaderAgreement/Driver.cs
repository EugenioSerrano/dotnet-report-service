using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfLayoutSpike;

namespace ReaderAgreement;

/// <summary>One run's record (runs.jsonl): one reader, one PDF, one repetition, in a fresh process.</summary>
internal sealed class RunLine
{
    public string Stage { get; set; } = string.Empty;

    public string Reader { get; set; } = string.Empty;

    public string Pdf { get; set; } = string.Empty;

    public int Repetition { get; set; }

    /// <summary>ok | timeout | killed-wall-clock | oom-killed | crashed | error | not-run.</summary>
    public string Class { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public int? ExitCode { get; set; }

    public string StartedAt { get; set; } = string.Empty;

    public double ProcessWallMs { get; set; }

    public double? ExtractMs { get; set; }

    public int? Pages { get; set; }

    public long? TreePeakRssKiB { get; set; }

    public long? VmHwmKiB { get; set; }

    public long? ManagedHeapPeakBytes { get; set; }

    /// <summary>The container's running peak up to this run (not a per-run figure).</summary>
    public long? ContainerRunningPeakBytes { get; set; }

    public long? OomKillBefore { get; set; }

    public long? OomKillAfter { get; set; }

    public Dictionary<string, string> Outputs { get; set; } = [];
}

/// <summary>
/// The child of one run: reads one PDF with one reader and writes its extraction
/// file(s). The last stdout line is a JSON object with its own measurements.
/// Exit codes: 0 ok, 124 own time cap, 3 the reader threw, 2 harness error.
/// </summary>
internal static class ExtractCommand
{
    public static async Task<int> RunAsync(string reader, string pdf, string id, string outDirectory, int repetition)
    {
        using var watchdog = new Timer(_ =>
        {
            Console.Out.Flush();
            Environment.Exit(124);
        }, null, TimeSpan.FromSeconds(Settings.RunCapSeconds), Timeout.InfiniteTimeSpan);
        long heapPeak = 0;
        using var heapSampler = new Timer(_ => InterlockedMax(ref heapPeak, GC.GetTotalMemory(false)), null, 0, Settings.SamplingIntervalMilliseconds);
        var sha = ExtractionFile.HashFile(pdf);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        int pages;
        try
        {
            switch (reader)
            {
                case Readers.Oracle:
                {
                    var text = await OracleExtractor.ExtractAsync(pdf, TimeSpan.FromSeconds(Settings.RunCapSeconds), CancellationToken.None);
                    watch.Stop();
                    pages = text.Pages;
                    outputs[Readers.OracleWords] = Write(ExtractionFile.From(reader, Readers.OracleWords, sha, text), Readers.OracleWords);
                    break;
                }

                case Readers.PdfPig:
                {
                    var (shipped, baseline, notes) = PdfPigExtractor.Extract(pdf);
                    watch.Stop();
                    pages = shipped.Pages;
                    var a = ExtractionFile.From(reader, Readers.PdfPigAsShipped, sha, shipped);
                    a.Notes = notes;
                    var b = ExtractionFile.From(reader, Readers.PdfPigBaseline, sha, baseline);
                    b.Notes = new Dictionary<string, string>(notes, StringComparer.Ordinal);
                    outputs[Readers.PdfPigAsShipped] = Write(a, Readers.PdfPigAsShipped);
                    outputs[Readers.PdfPigBaseline] = Write(b, Readers.PdfPigBaseline);
                    break;
                }

                case Readers.PdfSharp:
                {
                    var record = PdfSharpProbe.Probe(pdf, sha);
                    watch.Stop();
                    pages = record.Pages;
                    var path = Path.Combine(outDirectory, Readers.PdfSharp, $"{Safe(id)}.r{repetition}.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(record, Settings.Json));
                    outputs[Readers.PdfSharp] = path;
                    break;
                }

                default:
                    Console.Error.WriteLine($"Unknown reader {reader}.");
                    return 2;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"{e.GetType().FullName}: {e.Message.Split('\n')[0]}");
            Console.WriteLine(JsonSerializer.Serialize(new { error = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}" }, Settings.JsonLine));
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            extractMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
            pages,
            vmHwmKiB = ProcessTreeSampler.HarnessHighWaterMarkKiB(),
            managedHeapPeakBytes = Math.Max(heapPeak, GC.GetTotalMemory(false)),
            outputs = outputs.ToDictionary(kv => kv.Key, kv => Path.GetFileName(kv.Value)),
        }, Settings.JsonLine));
        return 0;

        string Write(ExtractionFile file, string configuration)
        {
            var path = Path.Combine(outDirectory, configuration, $"{Safe(id)}.r{repetition}.json");
            file.Write(path);
            return path;
        }
    }

    public static string Safe(string id) => id.Replace('/', '_');

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>
/// The stage driver. One container per stage; inside it, a fresh child process
/// per (reader, PDF, repetition) under the caps; the child's whole process tree
/// is sampled from /proc (poppler's own processes included for the reference reader).
/// </summary>
internal static class Driver
{
    public static async Task<int> RunAsync(string stage, string repositoryRoot, string workDirectory, string resultsDirectory)
    {
        var problem = Freeze.Check(resultsDirectory);
        if (problem is not null)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }

        var readers = stage switch
        {
            "oracle" => new[] { Readers.Oracle },
            "candidates" => new[] { Readers.PdfPig, Readers.PdfSharp },
            _ => [],
        };
        if (readers.Length == 0)
        {
            Console.Error.WriteLine($"Unknown stage {stage}.");
            return 2;
        }

        Dictionary<string, int>? referencePages = null;
        if (stage == "candidates")
        {
            var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(resultsDirectory, "oracle.json")));
            if (oracle?["trusted"]?.GetValue<bool>() != true)
            {
                Console.Error.WriteLine("The reference reading is not trusted (results/oracle.json); the candidate stage does not start.");
                return 1;
            }

            referencePages = oracle!["pdfs"]!.AsArray()
                .Where(p => p?["pages"] is not null)
                .ToDictionary(p => p!["id"]!.GetValue<string>(), p => p!["pages"]!.GetValue<int>(), StringComparer.Ordinal);
        }

        var regenerated = Regeneration.Read(resultsDirectory);
        var extractDirectory = Path.Combine(workDirectory, stage, "extract");
        var runsPath = Path.Combine(workDirectory, stage, "runs.jsonl");
        Directory.CreateDirectory(extractDirectory);
        var done = File.Exists(runsPath)
            ? File.ReadAllLines(runsPath).Select(l => JsonSerializer.Deserialize<RunLine>(l, Settings.Json)!).ToList()
            : [];
        var stageWatch = Stopwatch.StartNew();
        var entries = PdfSet.All
            .Select(e => (Entry: e, Path: PdfSet.PathOf(e, repositoryRoot, regenerated)))
            .ToList();

        foreach (var reader in readers)
        {
            var ordered = entries
                .OrderBy(e => referencePages?.GetValueOrDefault(e.Entry.Id) ?? 0)
                .ThenBy(e => e.Path is not null && File.Exists(e.Path) ? new FileInfo(e.Path).Length : 0)
                .ToList();
            var cappedLayouts = new Dictionary<string, (string Run, long Size)>(StringComparer.Ordinal);
            foreach (var (entry, path) in ordered)
            {
                var size = (referencePages?.GetValueOrDefault(entry.Id) ?? 0) * 1_000_000L + (path is not null && File.Exists(path) ? new FileInfo(path).Length : 0);
                for (var repetition = 1; repetition <= Settings.Repetitions[reader]; repetition++)
                {
                    if (done.Any(d => d.Reader == reader && d.Pdf == entry.Id && d.Repetition == repetition))
                    {
                        continue;
                    }

                    var line = new RunLine
                    {
                        Stage = stage, Reader = reader, Pdf = entry.Id, Repetition = repetition,
                        StartedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                    };
                    var first = done.FirstOrDefault(d => d.Reader == reader && d.Pdf == entry.Id && d.Repetition == 1);
                    if (path is null || !File.Exists(path))
                    {
                        line.Class = "not-run";
                        line.Reason = "no PDF (not regenerated)";
                    }
                    else if (stageWatch.Elapsed.TotalSeconds > Settings.StageBudgetSeconds)
                    {
                        line.Class = "not-run";
                        line.Reason = "stage budget";
                    }
                    else if (DirectorySize(workDirectory) > Settings.WorkFolderCeilingBytes)
                    {
                        line.Class = "not-run";
                        line.Reason = "work folder over 4 GiB (C-3)";
                    }
                    else if (cappedLayouts.TryGetValue(entry.Layout, out var cap) && size > cap.Size)
                    {
                        line.Class = "not-run";
                        line.Reason = $"after cap at {cap.Run} (C-2)";
                    }
                    else if (repetition > 1 && first is not null && (first.ProcessWallMs > Settings.NearCeilingSeconds * 1000.0 || first.TreePeakRssKiB > Settings.NearCeilingTreeKiB))
                    {
                        line.Class = "not-run";
                        line.Reason = "near ceiling (C-1)";
                    }
                    else
                    {
                        await RunChildAsync(line, reader, path, entry.Id, extractDirectory, repetition);
                    }

                    if (line.Class is "timeout" or "killed-wall-clock" or "oom-killed")
                    {
                        cappedLayouts.TryAdd(entry.Layout, (entry.Id, size));
                    }

                    done.Add(line);
                    await File.AppendAllTextAsync(runsPath, JsonSerializer.Serialize(line, Settings.JsonLine) + "\n");
                    Console.WriteLine($"{reader} {entry.Id} r{repetition}: {line.Class}{(line.Reason is null ? string.Empty : $" ({line.Reason})")}, {line.ExtractMs} ms, {line.Pages} pages, tree {line.TreePeakRssKiB} KiB");
                }
            }
        }

        Console.WriteLine($"Stage {stage}: {done.Count} records, {stageWatch.Elapsed.TotalSeconds:0} s.");
        return 0;
    }

    private static async Task RunChildAsync(RunLine line, string reader, string pdf, string id, string outDirectory, int repetition)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(typeof(Driver).Assembly.Location);
        foreach (var a in new[] { "extract", "--reader", reader, "--pdf", pdf, "--id", id, "--out", outDirectory, "--repetition", repetition.ToString(CultureInfo.InvariantCulture) })
        {
            info.ArgumentList.Add(a);
        }

        line.OomKillBefore = OomKills();
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the child.");
        var sampler = new TreeSampler(process.Id);
        sampler.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.KillAfterSeconds));
        var killed = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            killed = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync();
        }

        watch.Stop();
        line.TreePeakRssKiB = sampler.Stop();
        line.ProcessWallMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        line.OomKillAfter = OomKills();
        line.ContainerRunningPeakBytes = ProcessTreeSampler.CgroupMemoryPeakBytes();
        line.ExitCode = killed ? null : process.ExitCode;
        var output = await stdout;
        var errors = await stderr;
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        JsonNode? metrics = null;
        try
        {
            metrics = last is null ? null : JsonNode.Parse(last);
        }
        catch (JsonException)
        {
        }

        if (line.OomKillAfter > line.OomKillBefore)
        {
            line.Class = "oom-killed";
        }
        else if (killed)
        {
            line.Class = "killed-wall-clock";
        }
        else
        {
            line.Class = process.ExitCode switch
            {
                0 => "ok",
                124 => "timeout",
                3 => "crashed",
                137 => "oom-killed",
                _ => "error",
            };
        }

        if (line.Class != "ok")
        {
            line.Reason = metrics?["error"]?.GetValue<string>() ?? errors.Split('\n')[0].Trim();
        }

        if (metrics is not null && metrics["extractMs"] is not null)
        {
            line.ExtractMs = metrics["extractMs"]!.GetValue<double>();
            line.Pages = metrics["pages"]!.GetValue<int>();
            line.VmHwmKiB = metrics["vmHwmKiB"]?.GetValue<long?>();
            line.ManagedHeapPeakBytes = metrics["managedHeapPeakBytes"]?.GetValue<long>();
            foreach (var (key, value) in metrics["outputs"]!.AsObject())
            {
                var file = Path.Combine(outDirectory, key, value!.GetValue<string>());
                line.Outputs[key] = ExtractionFile.HashFile(file);
            }
        }
    }

    private static long? OomKills()
    {
        const string events = "/sys/fs/cgroup/memory.events";
        if (!File.Exists(events))
        {
            return null;
        }

        foreach (var l in File.ReadAllLines(events))
        {
            if (l.StartsWith("oom_kill ", StringComparison.Ordinal))
            {
                return long.Parse(l["oom_kill ".Length..], CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static long DirectorySize(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
}

/// <summary>Samples, every 50 ms, the summed resident memory of a process and all its descendants (Linux /proc).</summary>
internal sealed class TreeSampler(int rootPid)
{
    private Thread? thread;
    private volatile bool stopping;
    private long peak;

    public void Start()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        thread = new Thread(() =>
        {
            while (!stopping)
            {
                peak = Math.Max(peak, Sample());
                Thread.Sleep(Settings.SamplingIntervalMilliseconds);
            }
        })
        { IsBackground = true, Name = "tree-sampler" };
        thread.Start();
    }

    /// <summary>Stops and returns the peak in KiB (null off Linux).</summary>
    public long? Stop()
    {
        stopping = true;
        thread?.Join();
        return OperatingSystem.IsLinux() ? peak : null;
    }

    private long Sample()
    {
        var parents = new Dictionary<int, int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var close = stat.LastIndexOf(')');
                var fields = stat[(close + 2)..].Split(' ');
                parents[pid] = int.Parse(fields[1], CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException)
            {
            }
        }

        var tree = new HashSet<int> { rootPid };
        bool grew;
        do
        {
            grew = false;
            foreach (var (pid, parent) in parents)
            {
                if (!tree.Contains(pid) && tree.Contains(parent))
                {
                    tree.Add(pid);
                    grew = true;
                }
            }
        }
        while (grew);

        long total = 0;
        foreach (var pid in tree)
        {
            try
            {
                foreach (var l in File.ReadLines($"/proc/{pid}/status"))
                {
                    if (l.StartsWith("VmRSS:", StringComparison.Ordinal))
                    {
                        total += long.Parse(l["VmRSS:".Length..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
            }
        }

        return total;
    }
}
