using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NativeLayoutSpike;

/// <summary>One planned run: fixture, markup, repetition, optional control mutation, matrix group and size series.</summary>
internal sealed record PlannedRun(string RunId, string FixtureId, string Markup, int Repetition, string? Mutation, int Group, string? Series, int SeriesIndex);

/// <summary>
/// Runs the matrix in series, one fresh child process per run, and appends one
/// line per run to runs.jsonl as soon as the run ends. Caps: 300 s inside the
/// child, the child's process tree killed at 330 s, the container's memory cap
/// (an out-of-memory kill is read from the cgroup's oom_kill counter). A capped
/// run skips the larger sizes of its series and the remaining repetitions of its
/// fixture; a 3-hour matrix budget skips the runs not started by then. Every
/// skip is a recorded result.
/// </summary>
internal sealed class MatrixDriver(string fixturesDirectory, string outDirectory)
{
    public static readonly TimeSpan RunCap = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan KillAfter = TimeSpan.FromSeconds(330);
    public static readonly TimeSpan MatrixBudget = TimeSpan.FromHours(3);
    private static readonly string[] CapClasses = ["timeout", "killed-wall-clock", "oom-killed", "crashed"];

    public static List<PlannedRun> Plan(string matrix)
    {
        var runs = new List<PlannedRun>();
        void Add(string fixture, string markup, int reps, int group, string? series = null, int seriesIndex = 0, string? mutation = null)
        {
            for (var r = 1; r <= reps; r++)
            {
                var id = (mutation is null ? string.Empty : mutation + "-") + $"{fixture}-{markup}-r{r}";
                runs.Add(new PlannedRun(id, fixture, markup, r, mutation, group, series, seriesIndex));
            }
        }

        string[] both = [HtmlBuilder.Table, HtmlBuilder.Grid];
        if (matrix == "quick")
        {
            Add("B100", HtmlBuilder.Table, 1, 1);
            Add("B100", HtmlBuilder.Grid, 1, 1);
            Add("T300u", HtmlBuilder.Table, 1, 5, "uniq-table", 3);
            Add("F26K-b", HtmlBuilder.Table, 1, 9, "free-b-table", 2);
            Add("B100", HtmlBuilder.Table, 1, 2, mutation: "NC-W");
            return runs;
        }

        // 1 — controls base and positive controls.
        foreach (var fixture in new[] { "B100", "K1" })
        {
            foreach (var markup in both)
            {
                Add(fixture, markup, 1, 1);
            }
        }

        // 2 — HTML-level negative controls.
        foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
        {
            Add("B100", HtmlBuilder.Table, 1, 2, mutation: mutation);
        }

        Add("F26K-a", HtmlBuilder.Table, 1, 2, mutation: "NC-F");

        // 3 — the ordinary anchor block, three repetitions.
        foreach (var markup in both)
        {
            Add("B1000", markup, 3, 3);
        }

        // 4 — tall rows with the earlier spike's repeated text, and the giant-row fixture.
        foreach (var markup in both)
        {
            var i = 0;
            foreach (var fixture in new[] { "T150", "T200", "T300", "T500" })
            {
                Add(fixture, markup, 1, 4, $"rep-{markup}", ++i);
            }

            Add("G200", markup, 1, 4, $"giant-{markup}", 1);
        }

        // 5 — tall rows with unique text (the same series continues with the record-size fixtures, group 8).
        foreach (var markup in both)
        {
            var i = 0;
            foreach (var fixture in new[] { "T150u", "T200u", "T300u", "T500u" })
            {
                Add(fixture, markup, 1, 5, $"uniq-{markup}", ++i);
            }
        }

        // 6 — negative controls inside a multi-page row.
        foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
        {
            Add("T300u", HtmlBuilder.Table, 1, 6, mutation: mutation);
        }

        // 7 — bottom-aligned amount.
        foreach (var markup in both)
        {
            Add("T300u-ab", markup, 1, 7, $"uniq-ab-{markup}", 1);
            Add("X64K-ab", markup, 1, 7, $"uniq-ab-{markup}", 2);
        }

        // 8 — record sizes to the 1 MiB hypothesis, three repetitions.
        foreach (var markup in both)
        {
            var i = 4;
            foreach (var fixture in new[] { "X64K", "X128K", "X256K", "X512K", "X1M" })
            {
                Add(fixture, markup, 3, 8, $"uniq-{markup}", ++i);
            }
        }

        // 9 — free text, table markup.
        foreach (var (placement, sizes) in new[]
        {
            ("b", new[] { "F8K", "F26K", "F64K", "F256K", "F1M" }),
            ("a", new[] { "F8K", "F26K", "F64K", "F256K", "F1M" }),
            ("ba", new[] { "F26K" }),
            ("1p-b", new[] { "F26K", "F1M" }),
            ("1p-a", new[] { "F26K", "F1M" }),
        })
        {
            var i = 0;
            foreach (var size in sizes)
            {
                var fixture = $"{size}-{placement}";
                Add(fixture, HtmlBuilder.Table, fixture is "F1M-b" or "F1M-a" ? 3 : 1, 9, $"free-{placement}-table", ++i);
            }
        }

        // 10 — free text, markup cross-check.
        Add("F26K-ba", HtmlBuilder.Grid, 1, 10, "free-ba-grid", 1);
        Add("F1M-1p-b", HtmlBuilder.Grid, 1, 10, "free-1p-b-grid", 2);
        return runs;
    }

    public async Task<List<RunRecord>> ExecuteAsync(string matrix, bool resume)
    {
        var plan = Plan(matrix);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "plan.json"), JsonSerializer.Serialize(new { matrix, runs = plan }, new JsonSerializerOptions(RunRecord.Json) { WriteIndented = true }) + "\n");
        var runsPath = Path.Combine(outDirectory, "runs.jsonl");
        var records = new Dictionary<string, RunRecord>(StringComparer.Ordinal);
        if (resume && File.Exists(runsPath))
        {
            foreach (var line in File.ReadLines(runsPath).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var record = JsonSerializer.Deserialize<RunRecord>(line, RunRecord.Json)!;
                records[record.RunId] = record;
            }

            Console.WriteLine($"Resuming: {records.Count} runs already recorded.");
        }
        else if (File.Exists(runsPath))
        {
            File.Delete(runsPath);
        }

        Directory.CreateDirectory(Path.Combine(outDirectory, "pdf"));
        Directory.CreateDirectory(Path.Combine(outDirectory, "records"));
        var capped = new Dictionary<string, (int Index, string RunId)>(StringComparer.Ordinal);
        var cappedFixtures = new Dictionary<string, string>(StringComparer.Ordinal);
        var started = Stopwatch.StartNew();
        await using var writer = new StreamWriter(runsPath, append: true) { AutoFlush = true, NewLine = "\n" };
        foreach (var planned in plan)
        {
            var fixtureKey = $"{planned.Mutation}/{planned.FixtureId}/{planned.Markup}";
            if (records.TryGetValue(planned.RunId, out var existing))
            {
                NoteCap(existing, planned);
                continue;
            }

            RunRecord record;
            if (cappedFixtures.TryGetValue(fixtureKey, out var cappedRun)
                || (planned.Series is not null && capped.TryGetValue(planned.Series, out var cap) && planned.SeriesIndex > cap.Index && (cappedRun = cap.RunId) is not null))
            {
                record = NotRun(planned, $"after cap at {cappedRun}");
            }
            else if (started.Elapsed > MatrixBudget)
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
                $"{record.RunId}: {record.Outcome}{(record.Reason is null ? string.Empty : $" ({record.Reason})")}, pages~{record.PdfPagesHeuristic}, nav {record.NavigationMs:0} ms, pdf {record.PdfMs:0} ms, wall {record.WallMs:0} ms, chromium peak {m?.ChromiumTreePeakRssKiB / 1024} MiB"));
        }

        return [.. plan.Select(p => records[p.RunId])];

        void NoteCap(RunRecord record, PlannedRun planned)
        {
            if (!CapClasses.Contains(record.Outcome))
            {
                return;
            }

            cappedFixtures[$"{planned.Mutation}/{planned.FixtureId}/{planned.Markup}"] = record.RunId;
            if (planned.Series is not null && !capped.ContainsKey(planned.Series))
            {
                capped[planned.Series] = (planned.SeriesIndex, record.RunId);
            }
        }
    }

    private static RunRecord NotRun(PlannedRun planned, string reason) => new()
    {
        RunId = planned.RunId,
        FixtureId = planned.FixtureId,
        Markup = planned.Markup,
        Repetition = planned.Repetition,
        Mutation = planned.Mutation,
        Group = planned.Group,
        Series = planned.Series,
        Outcome = "not-run",
        Reason = reason,
    };

    private async Task<RunRecord> RunChildAsync(PlannedRun planned)
    {
        var recordPath = Path.Combine(outDirectory, "records", planned.RunId + ".json");
        if (File.Exists(recordPath))
        {
            File.Delete(recordPath);
        }

        var oomBefore = OomKills();
        var startedAt = DateTimeOffset.Now;
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "NativeLayoutSpike.dll"));
        foreach (var argument in new[]
        {
            "run-one", "--fixtures", fixturesDirectory, "--out", outDirectory, "--run-id", planned.RunId, "--fixture", planned.FixtureId,
            "--markup", planned.Markup, "--repetition", planned.Repetition.ToString(CultureInfo.InvariantCulture),
            "--group", planned.Group.ToString(CultureInfo.InvariantCulture), "--series", planned.Series ?? "-", "--mutation", planned.Mutation ?? "-",
        })
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
