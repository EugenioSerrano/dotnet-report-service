using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PreSplitLayoutSpike;

/// <summary>
/// Runs one stage of the matrix (or the quick loop) in series, one fresh child
/// process per run, and appends one line per run to runs.jsonl as soon as the
/// run ends. Caps, failure classes, the after-cap rule and resume follow the
/// native-layout experiment (spikes/07-native-tall-rows): 300 s inside the
/// child, the child's process tree killed at 330 s, the container's memory cap
/// read through the cgroup's oom_kill counter. The driver's wall-clock budget
/// is given per stage; runs not started within it are "not-run (matrix budget)".
/// </summary>
internal sealed class MatrixDriver(string fixturesDirectory, string outDirectory)
{
    public static readonly TimeSpan RunCap = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan KillAfter = TimeSpan.FromSeconds(330);
    private static readonly string[] CapClasses = ["timeout", "killed-wall-clock", "oom-killed", "crashed"];

    public string RunsPath => Path.Combine(outDirectory, "runs.jsonl");

    /// <summary>Writes plan.json in the checker's contract (matrix name and the planned runs).</summary>
    public static async Task WritePlanAsync(string directory, string matrix, IEnumerable<PlannedRun> plan)
    {
        var document = new
        {
            matrix,
            runs = plan.Select(p => new
            {
                p.RunId,
                p.FixtureId,
                markup = "grid",
                p.Repetition,
                p.Mutation,
                p.Split,
                p.Trigger,
                p.PartLimit,
                p.FooterOn,
                p.Stage,
                p.MatrixRow,
                matrixRows = p.MatrixRows,
                p.Series,
                p.SeriesIndex,
            }),
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(document, RunRecord.JsonIndented) + "\n");
    }

    /// <summary>Elapsed driver time already recorded in runs.jsonl (sum of the runs' wall clock), for a resumed stage.</summary>
    public static double RecordedWallSeconds(string runsPath) => File.Exists(runsPath)
        ? File.ReadLines(runsPath).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonSerializer.Deserialize<RunRecord>(l, RunRecord.Json)!).Sum(r => r.WallMs ?? 0) / 1000.0
        : 0;

    public async Task<List<RunRecord>> ExecuteAsync(string matrix, List<PlannedRun> plan, bool resume, TimeSpan budget)
    {
        await WritePlanAsync(outDirectory, matrix, plan);
        var records = new Dictionary<string, RunRecord>(StringComparer.Ordinal);
        if (resume && File.Exists(RunsPath))
        {
            foreach (var line in File.ReadLines(RunsPath).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var record = JsonSerializer.Deserialize<RunRecord>(line, RunRecord.Json)!;
                records[record.RunId] = record;
            }

            Console.WriteLine($"Resuming: {records.Count} runs already recorded.");
        }
        else if (File.Exists(RunsPath))
        {
            File.Delete(RunsPath);
        }

        foreach (var folder in new[] { "pdf", "records", "html", "layout" })
        {
            Directory.CreateDirectory(Path.Combine(outDirectory, folder));
        }

        var capped = new Dictionary<string, (int Index, string RunId)>(StringComparer.Ordinal);
        var cappedFixtures = new Dictionary<string, string>(StringComparer.Ordinal);
        var started = Stopwatch.StartNew();
        await using var writer = new StreamWriter(RunsPath, append: true) { AutoFlush = true, NewLine = "\n" };
        foreach (var planned in plan)
        {
            if (records.TryGetValue(planned.RunId, out var existing))
            {
                NoteCap(existing, planned);
                continue;
            }

            RunRecord record;
            if (cappedFixtures.TryGetValue(FixtureKey(planned), out var cappedRun)
                || (planned.Series is not null && capped.TryGetValue(planned.Series, out var cap) && planned.SeriesIndex > cap.Index && (cappedRun = cap.RunId) is not null))
            {
                record = NotRun(planned, $"after cap at {cappedRun}");
            }
            else if (started.Elapsed > budget)
            {
                record = NotRun(planned, "matrix budget");
            }
            else
            {
                record = await RunChildAsync(planned);
            }

            records[planned.RunId] = record;
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, RunRecord.Json));
            NoteCap(record, planned);
            var m = record.Memory;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{record.RunId}: {record.Outcome}{(record.Reason is null ? string.Empty : $" ({record.Reason})")}, pages {record.Pages}, split rows {record.SplitResult?["splitRows"]?.AsArray().Count}, parts {record.SplitResult?["partsTotal"]}, "
                + $"measure {record.MeasureMs:0} ms, split {record.SplitMs:0} ms, layout {record.LayoutMs:0} ms, pdf {record.PdfMs:0} ms, wall {record.WallMs:0} ms, chromium peak {m?.ChromiumTreePeakRssKiB / 1024} MiB, "
                + $"rules {string.Join(' ', record.Rules.Select(r => r.Pass ? "P" : "F"))}, layout repeated {(record.LayoutRepeatedError == true ? "error" : "-")}/{(record.LayoutRepeatedAnyLevel == true ? "any" : "-")}, SEM {(record.Sem?.Pass == true ? "pass" : "FAIL")}"));
        }

        Console.WriteLine($"Stage driver wall clock {started.Elapsed.TotalSeconds:0.0} s (budget {budget.TotalSeconds:0} s).");
        return [.. plan.Select(p => records[p.RunId])];

        void NoteCap(RunRecord record, PlannedRun planned)
        {
            if (!CapClasses.Contains(record.Outcome))
            {
                return;
            }

            cappedFixtures[FixtureKey(planned)] = record.RunId;
            if (planned.Series is not null && !capped.ContainsKey(planned.Series))
            {
                capped[planned.Series] = (planned.SeriesIndex, record.RunId);
            }
        }
    }

    private static string FixtureKey(PlannedRun p) => $"{p.Mutation}/{p.FixtureId}/{p.Split}/{p.Trigger}/{p.PartLimit}/{p.FooterOn}";

    public static RunRecord FromPlan(PlannedRun planned) => new()
    {
        RunId = planned.RunId,
        FixtureId = planned.FixtureId,
        Repetition = planned.Repetition,
        Mutation = planned.Mutation,
        Marks = planned.FooterOn ? "expected" : "none",
        Footer = planned.FooterOn ? "accounting" : "none",
        Stage = planned.Stage,
        MatrixRow = planned.MatrixRow,
        Series = planned.Series,
        SeriesIndex = planned.SeriesIndex,
        MatrixRows = [.. planned.MatrixRows],
        Split = planned.Split,
        Trigger = planned.Trigger,
        PartLimit = planned.PartLimit,
    };

    private static RunRecord NotRun(PlannedRun planned, string reason)
    {
        var record = FromPlan(planned);
        record.Outcome = "not-run";
        record.Reason = reason;
        return record;
    }

    private async Task<RunRecord> RunChildAsync(PlannedRun planned)
    {
        var recordPath = Path.Combine(outDirectory, "records", planned.RunId + ".json");
        var specPath = Path.Combine(outDirectory, "records", planned.RunId + ".plan.json");
        if (File.Exists(recordPath))
        {
            File.Delete(recordPath);
        }

        await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(planned, RunRecord.Json));
        var oomBefore = OomKills();
        var startedAt = DateTimeOffset.Now;
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "PreSplitLayoutSpike.dll"));
        foreach (var argument in new[] { "run-one", "--fixtures", fixturesDirectory, "--out", outDirectory, "--plan", specPath })
        {
            info.ArgumentList.Add(argument);
        }

        var watch = Stopwatch.StartNew();
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Could not start the run process.");
        var killed = false;
        using (var deadline = new CancellationTokenSource(KillAfter))
        {
            try
            {
                await child.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                killed = true;
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                await child.WaitForExitAsync();
            }
        }

        var wall = watch.Elapsed.TotalMilliseconds;
        var leftovers = KillLeftovers();
        var oomAfter = OomKills();
        RunRecord record;
        if (File.Exists(recordPath))
        {
            record = JsonSerializer.Deserialize<RunRecord>(await File.ReadAllTextAsync(recordPath), RunRecord.Json)!;
        }
        else
        {
            record = NotRun(planned, "the run process ended without a record");
            record.Outcome = "error";
        }

        record.StartedAt = startedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        record.WallMs = wall;
        record.ChildExitCode = killed ? null : child.ExitCode;
        record.OomKillBefore = oomBefore;
        record.OomKillAfter = oomAfter;
        record.LeftoverProcessesKilled = leftovers;
        if (killed)
        {
            record.Outcome = "killed-wall-clock";
            record.Reason = $"the run's process tree was killed at {KillAfter.TotalSeconds:0} s";
            record.Pdf = null;
        }

        if (oomBefore is not null && oomAfter is not null && oomAfter > oomBefore)
        {
            record.Outcome = "oom-killed";
            record.Reason = $"the container's oom_kill counter rose from {oomBefore} to {oomAfter} during the run";
            record.Pdf = null;
        }

        return record;
    }

    /// <summary>The oom_kill counter of the container's memory cgroup (cgroup v2), when readable.</summary>
    private static long? OomKills()
    {
        try
        {
            foreach (var line in File.ReadLines("/sys/fs/cgroup/memory.events"))
            {
                if (line.StartsWith("oom_kill ", StringComparison.Ordinal))
                {
                    return long.Parse(line[9..].Trim(), CultureInfo.InvariantCulture);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
        }

        return null;
    }

    /// <summary>
    /// Kills every process of the container other than the init process and this
    /// driver: what a finished, timed-out or killed run left behind (browser
    /// processes re-parented to init). Linux only; returns how many were killed.
    /// </summary>
    private static int KillLeftovers()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        var self = Environment.ProcessId;
        var killed = 0;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == 1 || pid == self)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                killed++;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        return killed;
    }
}
