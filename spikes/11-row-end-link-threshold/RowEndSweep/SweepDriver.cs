using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PaginationFixtures;
using TwoPassSpike;

namespace RowEndSweep;

/// <summary>
/// Runs a round of the sweep in series, inside the unchanged two-pass harness image.
/// Per run: the harness's own "layout-one" (native print with the row markers) then,
/// when it is "ok", its own "stamp-one --read-only" (the reading of the markers and
/// the guard; nothing is stamped or saved), each a fresh child process started with
/// exactly the argument list of the earlier experiment's driver for its marker
/// probes. Caps per pass: 300 s inside the child, the child's process tree killed at
/// 330 s, the container's memory cap (an out-of-memory kill read from the cgroup's
/// oom_kill counter before and after each pass). A capped pass skips the larger
/// sizes of its series; a 120-minute round budget skips the runs not started by
/// then. Every skip is a recorded result. Three files are flushed per run:
/// twopass.jsonl and runs.jsonl (the formats the two unchanged checkers read) and
/// argv.jsonl (every child's full command line). The child handling is copied from
/// spikes/09-two-pass-page-subtotals (TwoPassSpike/MatrixDriver.cs); only the plan
/// source and the harness path (the image's /app, not this program's folder) differ.
/// </summary>
internal sealed class SweepDriver(string fixturesDirectory, string outDirectory, string fontFile)
{
    /// <summary>The harness inside the image; the earlier driver ran from that folder itself.</summary>
    public const string HarnessDll = "/app/TwoPassSpike.dll";

    public static readonly TimeSpan KillAfter = TimeSpan.FromSeconds(330);
    public static readonly TimeSpan RoundBudget = TimeSpan.FromMinutes(120);
    private static readonly string[] CapClasses = ["timeout", "killed-wall-clock", "oom-killed", "crashed"];
    private static readonly JsonSerializerOptions Compact = new(RunRecord.Json) { WriteIndented = false };

    public static async Task<int> RunAsync(string fixtures, string outDirectory, string round, IReadOnlyList<string>? extra)
    {
        var plan = SweepPlan.Build(round, extra);
        var sums = Sums.Read(Path.Combine(fixtures, FixtureFile.SumsFileName));
        foreach (var id in plan.Select(p => p.FixtureId).Distinct(StringComparer.Ordinal))
        {
            try
            {
                _ = FixtureFile.ReadVerified(fixtures, id, sums);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        var font = await FontFileAsync();
        Directory.CreateDirectory(outDirectory);
        var startedAt = DateTimeOffset.Now;
        Console.WriteLine($"Fixtures verified. Round '{round}', {plan.Count} runs, {Environment.ProcessorCount} CPUs, memory.max {ReadCgroup("memory.max") ?? "n/a"}, font {font}.");
        var records = await new SweepDriver(fixtures, outDirectory, font).ExecuteAsync(plan);
        var driver = new Dictionary<string, object?>
        {
            ["round"] = round,
            ["startedAt"] = startedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["endedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["runs"] = plan.Count,
            ["processorCount"] = Environment.ProcessorCount,
            ["memoryMax"] = ReadCgroup("memory.max"),
            ["fontFile"] = font,
            ["processPath"] = Environment.ProcessPath,
            ["harnessDll"] = HarnessDll,
            ["uid"] = ReadUid(),
            ["pass1Outcomes"] = records.GroupBy(r => r.Pass1.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            ["pass2Outcomes"] = records.GroupBy(r => r.Pass2.Outcome).ToDictionary(g => g.Key, g => g.Count()),
        };
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "driver.json"), JsonSerializer.Serialize(driver, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"{records.Count} runs recorded. Pass 1: " + string.Join(", ", records.GroupBy(r => r.Pass1.Outcome).Select(g => $"{g.Count()} {g.Key}"))
            + ". Pass 2: " + string.Join(", ", records.GroupBy(r => r.Pass2.Outcome).Select(g => $"{g.Count()} {g.Key}")) + ".");
        return 0;
    }

    private async Task<List<TwoPassRecord>> ExecuteAsync(List<SweepRun> plan)
    {
        await File.WriteAllTextAsync(
            Path.Combine(outDirectory, "plan.json"),
            JsonSerializer.Serialize(new { round = plan.FirstOrDefault()?.Round, runs = plan }, new JsonSerializerOptions(RunRecord.Json) { WriteIndented = true }) + "\n");
        foreach (var folder in new[] { "pdf", "records", "readback", "stamp" })
        {
            Directory.CreateDirectory(Path.Combine(outDirectory, folder));
        }

        var twoPassPath = Path.Combine(outDirectory, "twopass.jsonl");
        var runsPath = Path.Combine(outDirectory, "runs.jsonl");
        var argvPath = Path.Combine(outDirectory, "argv.jsonl");
        File.Delete(twoPassPath);
        File.Delete(runsPath);
        File.Delete(argvPath);
        var records = new List<TwoPassRecord>();
        var capped = new Dictionary<string, (int Index, string Run)>(StringComparer.Ordinal);
        var started = Stopwatch.StartNew();
        await using var twoPass = new StreamWriter(twoPassPath, append: true) { AutoFlush = true, NewLine = "\n" };
        await using var runsFile = new StreamWriter(runsPath, append: true) { AutoFlush = true, NewLine = "\n" };
        await using var argv = new StreamWriter(argvPath, append: true) { AutoFlush = true, NewLine = "\n" };
        foreach (var planned in plan)
        {
            TwoPassRecord record;
            if (capped.TryGetValue(planned.Series, out var cap) && planned.SeriesIndex > cap.Index)
            {
                record = NotRun(planned, $"after cap at {cap.Run}");
            }
            else if (started.Elapsed > RoundBudget)
            {
                record = NotRun(planned, "round budget");
            }
            else
            {
                var pass1 = await RunChildAsync(planned, 1, argv);
                var pass2 = pass1.Outcome == "ok"
                    ? await RunChildAsync(planned, 2, argv)
                    : new PassRecord { Outcome = "not-run", Reason = $"pass 1 {pass1.Outcome}" };
                record = Record(planned, pass1, pass2);
            }

            records.Add(record);
            await twoPass.WriteLineAsync(JsonSerializer.Serialize(record, RunRecord.Json));
            await runsFile.WriteLineAsync(JsonSerializer.Serialize(ContractLine(record), RunRecord.Json));
            if ((CapClasses.Contains(record.Pass1.Outcome) || CapClasses.Contains(record.Pass2.Outcome)) && !capped.ContainsKey(planned.Series))
            {
                capped[planned.Series] = (planned.SeriesIndex, record.Run);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{record.Run}: pass 1 {record.Pass1.Outcome}{Reason(record.Pass1)}, pages {record.Pass1.Pages}, pdf {record.Pass1.PdfMs:0} ms, chromium peak {record.Pass1.Memory?.ChromiumTreePeakRssKiB / 1024} MiB; "
                + $"reading {record.Pass2.Outcome}{Reason(record.Pass2)}"));
        }

        return records;
    }

    private static string Reason(PassRecord pass) => pass.Reason is null ? string.Empty : $" ({pass.Reason})";

    private static TwoPassRecord Record(SweepRun planned, PassRecord pass1, PassRecord pass2) => new()
    {
        Run = planned.Run,
        FixtureId = planned.FixtureId,
        Markup = planned.Markup,
        Strategy = SweepPlan.Cell,
        Repetition = planned.Repetition,
        Mutation = null,
        Fault = null,
        Group = planned.Round,
        Series = planned.Series,
        Pass1 = pass1,
        Pass2 = pass2,
    };

    private static TwoPassRecord NotRun(SweepRun planned, string reason) => Record(
        planned,
        new PassRecord { Outcome = "not-run", Reason = reason },
        new PassRecord { Outcome = "not-run", Reason = reason });

    /// <summary>The runs.jsonl line of a run: its pass-1 PDF in strategy "cell" (no marks, no footer expected); a read-only second pass writes no PDF, so it has no line.</summary>
    private static RunRecord ContractLine(TwoPassRecord record) => new()
    {
        RunId = $"{record.Run}.p1",
        FixtureId = record.FixtureId,
        Markup = record.Markup,
        Repetition = record.Repetition,
        Mutation = record.Mutation,
        Marks = "none",
        Footer = "none",
        Outcome = record.Pass1.Outcome == "guard-failed" ? "not-run" : record.Pass1.Outcome,
        Reason = record.Pass1.Outcome == "guard-failed" ? $"guard-failed: {record.Pass1.Reason}" : record.Pass1.Reason,
        Pdf = record.Pass1.Outcome == "ok" ? record.Pass1.Pdf : null,
        Dialogs = record.Pass1.Dialogs,
        AbortedRequests = record.Pass1.AbortedRequests,
        Scripts = record.Pass1.Scripts,
        Run = record.Run,
        Pass = 1,
        Strategy = record.Strategy,
        Fault = record.Fault,
    };

    private async Task<PassRecord> RunChildAsync(SweepRun planned, int pass, StreamWriter argvLog)
    {
        var recordPath = Path.Combine(outDirectory, "records", $"{planned.Run}.p{pass}.json");
        if (File.Exists(recordPath))
        {
            File.Delete(recordPath);
        }

        var oomBefore = OomKills();
        var startedAt = DateTimeOffset.Now;
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { UseShellExecute = false };
        info.ArgumentList.Add(HarnessDll);
        var arguments = new List<string>
        {
            pass == 1 ? "layout-one" : "stamp-one", "--fixtures", fixturesDirectory, "--out", outDirectory, "--run", planned.Run,
            "--fixture", planned.FixtureId, "--markup", planned.Markup, "--strategy", SweepPlan.Cell,
            "--mutation", "-", "--fault", "-", "--font", fontFile,
        };
        if (pass == 2)
        {
            arguments.Add("--read-only");
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        await argvLog.WriteLineAsync(JsonSerializer.Serialize(new { run = planned.Run, pass, fileName = info.FileName, arguments = info.ArgumentList.ToList() }, Compact));

        var watch = Stopwatch.StartNew();
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Could not start the pass process.");
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
        PassRecord record;
        if (File.Exists(recordPath))
        {
            record = JsonSerializer.Deserialize<PassRecord>(await File.ReadAllTextAsync(recordPath), RunRecord.Json)!;
        }
        else
        {
            record = new PassRecord { Outcome = "error", Reason = "the pass process ended without a record" };
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
            record.Reason = $"the pass's process tree was killed at {KillAfter.TotalSeconds:0} s";
            record.Pdf = null;
        }

        if (oomBefore is not null && oomAfter is not null && oomAfter > oomBefore)
        {
            record.Outcome = "oom-killed";
            record.Reason = $"the container's oom_kill counter rose from {oomBefore} to {oomAfter} during the pass";
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
    /// driver: what a finished, timed-out or killed pass left behind (browser
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

    /// <summary>The regular Liberation Sans file as fontconfig resolves it in the image (the face the first pass prints with); the harness's own default otherwise.</summary>
    private static async Task<string> FontFileAsync()
    {
        const string defaultFile = "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
        try
        {
            var info = new ProcessStartInfo("fc-match") { RedirectStandardOutput = true, UseShellExecute = false };
            info.ArgumentList.Add("--format=%{file}");
            info.ArgumentList.Add("Liberation Sans:style=Regular");
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return process.ExitCode == 0 && File.Exists(output.Trim()) ? output.Trim() : defaultFile;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return defaultFile;
        }
    }

    private static string? ReadCgroup(string name)
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

    private static string? ReadUid()
    {
        try
        {
            return File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal))?[4..].Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
