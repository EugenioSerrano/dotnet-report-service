using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TwoPassSpike;

/// <summary>One planned run: fixture, markup, amount strategy, repetition, optional control mutation or fault, matrix group and size series.</summary>
internal sealed record PlannedRun(
    string Run, string FixtureId, string Markup, string Strategy, int Repetition, string? Mutation, string? Fault, int Group, string? Series, int SeriesIndex,
    string? NotRunReason = null);

/// <summary>A planned run replaced by its strategy "cell" twin because its markup failed the position check of the marker probe.</summary>
internal sealed record ReplacedRun(string Run, string FixtureId, string Markup, string Strategy, int Repetition, int Group, string ReplacedBy, string Reason);

/// <summary>
/// Runs a matrix in series. Per run: pass 1 then, when pass 1 is "ok", pass 2,
/// each a fresh child process. Caps per pass: 300 s inside the child, the child's
/// process tree killed at 330 s, the container's memory cap (an out-of-memory kill
/// is read from the cgroup's oom_kill counter before and after each pass). A
/// capped pass skips the larger sizes of its series and the remaining repetitions
/// of its fixture; a 3-hour matrix budget skips the runs not started by then.
/// Every skip is a recorded result. Two files are flushed per run: runs.jsonl (the
/// text-integrity checker's contract, one line per PDF) and twopass.jsonl (one
/// line per run, both passes). Copied from spikes/07-native-tall-rows
/// (MatrixDriver.cs) and changed for the two passes, the strategies and faults.
/// </summary>
internal sealed class MatrixDriver(string fixturesDirectory, string outDirectory, string fontFile, IReadOnlyCollection<string> stampedMarkups)
{
    public const string Full = "full";
    public const string Quick = "quick";
    public const string ProbeMarkers = "probe-markers";
    public const string ProbeStamp = "probe-stamp";
    public const string ProbeSplitRows = "probe-split-rows";
    public const string ProbeStampedAb = "probe-stamped-ab";

    public static readonly TimeSpan PassCap = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan KillAfter = TimeSpan.FromSeconds(330);
    public static readonly TimeSpan MatrixBudget = TimeSpan.FromHours(3);
    private static readonly string[] CapClasses = ["timeout", "killed-wall-clock", "oom-killed", "crashed"];

    public static bool IsMatrix(string? name) => name is Full or Quick or ProbeMarkers or ProbeStamp or ProbeSplitRows or ProbeStampedAb;

    public static string RunId(string fixture, string markup, string strategy, int repetition, string? control) =>
        $"{fixture}-{markup}-{(strategy == HtmlBuilder.Stamped ? 's' : 'c')}-" + (control ?? $"r{repetition}");

    public static List<PlannedRun> Plan(string matrix)
    {
        var runs = new List<PlannedRun>();
        const string t = HtmlBuilder.Table;
        const string g = HtmlBuilder.Grid;
        const string s = HtmlBuilder.Stamped;
        const string c = HtmlBuilder.Cell;
        string[] both = [t, g];
        string[] strategies = [s, c];

        void Add(string fixture, string markup, string strategy, int reps, int group, string? series = null, int seriesIndex = 0, string? mutation = null, string? fault = null)
        {
            for (var r = 1; r <= reps; r++)
            {
                runs.Add(new PlannedRun(RunId(fixture, markup, strategy, r, mutation ?? fault), fixture, markup, strategy, r, mutation, fault, group, series, seriesIndex));
            }
        }

        switch (matrix)
        {
            case ProbeMarkers:
                return Probe.Markers();
            case ProbeStamp:
                return Probe.Stamp();
            case ProbeSplitRows:
                return Probe.SplitRows();
            case ProbeStampedAb:
                return Probe.StampedAb();
            case Quick:
                Add("B100", t, s, 1, 1);
                Add("B100", t, c, 1, 1);
                Add("T300u", t, s, 1, 7);
                Add("T300u-ab", g, c, 1, 8);
                Add("F26K-b", t, s, 1, 10);
                Add("B100", t, s, 1, 3, fault: "AC-1");
                return runs;
            default:
                break;
        }

        // 1 — positive controls.
        foreach (var fixture in new[] { "B100", "K1" })
        {
            foreach (var markup in both)
            {
                foreach (var strategy in strategies)
                {
                    Add(fixture, markup, strategy, 1, 1);
                }
            }
        }

        // 2 — negative controls of the reused text-integrity check (HTML-level, first pass).
        foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
        {
            Add("B100", t, c, 1, 2, mutation: mutation);
        }

        // 3 — accounting and read-back controls (second-pass faults).
        foreach (var (fault, fixture) in Faults.All)
        {
            Add(fixture, t, s, 1, 3, fault: fault);
        }

        // 4 — the ordinary anchor block, three repetitions.
        foreach (var markup in both)
        {
            foreach (var strategy in strategies)
            {
                Add("B1000", markup, strategy, 3, 4);
            }
        }

        // 5 — the ordinary size series.
        foreach (var markup in both)
        {
            var i = 0;
            foreach (var fixture in new[] { "B250", "B500", "B2000", "B4000" })
            {
                Add(fixture, markup, s, 1, 5, $"ord-{markup}-s", ++i);
            }
        }

        // 6 — tall rows with the earlier spike's repeated text, and the giant-row fixture.
        foreach (var markup in both)
        {
            var i = 0;
            foreach (var fixture in new[] { "T150", "T200", "T300", "T500" })
            {
                Add(fixture, markup, s, 1, 6, $"rep-{markup}-s", ++i);
            }

            Add("G200", markup, s, 1, 6, $"giant-{markup}-s", 1);
        }

        // 7 — tall rows with unique text (the series continues with the record-size fixtures, group 9); T300u also in "cell".
        foreach (var markup in both)
        {
            var i = 0;
            foreach (var fixture in new[] { "T150u", "T200u", "T300u", "T500u" })
            {
                Add(fixture, markup, s, 1, 7, $"uniq-{markup}-s", ++i);
            }

            Add("T300u", markup, c, 1, 7, $"uniq-{markup}-c", 3);
        }

        // 8 — bottom-aligned amount in a split row, both markups, both strategies.
        foreach (var markup in both)
        {
            foreach (var strategy in strategies)
            {
                var key = strategy == s ? 's' : 'c';
                Add("T300u-ab", markup, strategy, 1, 8, $"uniq-ab-{markup}-{key}", 1);
                Add("X64K-ab", markup, strategy, 1, 8, $"uniq-ab-{markup}-{key}", 2);
            }
        }

        // 9 — record sizes to the 1 MiB hypothesis; X1M three repetitions.
        foreach (var markup in both)
        {
            var i = 4;
            foreach (var fixture in new[] { "X64K", "X128K", "X256K", "X512K", "X1M" })
            {
                Add(fixture, markup, s, fixture == "X1M" ? 3 : 1, 9, $"uniq-{markup}-s", ++i);
            }
        }

        // 10 — free text, table markup.
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
                Add(fixture, t, s, fixture is "F1M-b" or "F1M-a" ? 3 : 1, 10, $"free-{placement}-table-s", ++i);
            }
        }

        // 11 — free text, markup cross-check.
        Add("F26K-ba", g, s, 1, 11, "free-ba-grid-s", 1);
        Add("F1M-1p-b", g, s, 1, 11, "free-1p-b-grid-s", 2);
        return runs;
    }

    /// <summary>
    /// The plan after the marker probe's position check: in a markup where strategy
    /// "stamped" may not run, the "stamped" runs of groups 5, 6, 9 and 10 are replaced
    /// by the same fixture and repetition in strategy "cell" (the replaced runs are
    /// listed apart and recorded "not-run"); the faults (and the quick plan's
    /// "stamped" runs) move to the markup where "stamped" runs; every other "stamped"
    /// run of that markup stays in the plan and is recorded "not-run" with the reason.
    /// </summary>
    public static (List<PlannedRun> Runs, List<ReplacedRun> Replaced) Arrange(string matrix, IReadOnlyCollection<string> stamped)
    {
        var plan = Plan(matrix);
        if (matrix is not (Full or Quick) || (stamped.Contains(HtmlBuilder.Table) && stamped.Contains(HtmlBuilder.Grid)))
        {
            return (plan, []);
        }

        var target = stamped.Contains(HtmlBuilder.Table) ? HtmlBuilder.Table : HtmlBuilder.Grid;
        var runs = new List<PlannedRun>();
        var replaced = new List<ReplacedRun>();
        foreach (var p in plan)
        {
            if (p.Strategy != HtmlBuilder.Stamped || stamped.Contains(p.Markup))
            {
                runs.Add(p);
                continue;
            }

            var reason = $"stamped not run in {p.Markup} markup: probe 0.6 (d2)";
            if (p.Fault is not null || matrix == Quick)
            {
                runs.Add(p with { Markup = target, Run = RunId(p.FixtureId, target, p.Strategy, p.Repetition, p.Mutation ?? p.Fault) });
                continue;
            }

            if (p.Group is 5 or 6 or 9 or 10)
            {
                var cell = p with
                {
                    Strategy = HtmlBuilder.Cell,
                    Run = RunId(p.FixtureId, p.Markup, HtmlBuilder.Cell, p.Repetition, null),
                    Series = p.Series is null ? null : p.Series[..^1] + "c",
                };
                runs.Add(cell);
                replaced.Add(new ReplacedRun(p.Run, p.FixtureId, p.Markup, p.Strategy, p.Repetition, p.Group, cell.Run, $"{reason}; replaced by {cell.Run}"));
                continue;
            }

            runs.Add(p with { NotRunReason = reason });
        }

        return (runs, replaced);
    }

    public async Task<List<TwoPassRecord>> ExecuteAsync(string matrix, bool resume)
    {
        var (plan, replaced) = Arrange(matrix, stampedMarkups);
        var readOnlySecondPass = matrix is ProbeMarkers or ProbeSplitRows or ProbeStampedAb;
        await File.WriteAllTextAsync(
            Path.Combine(outDirectory, "plan.json"),
            JsonSerializer.Serialize(new { matrix, stampedMarkups, runs = plan, replaced }, new JsonSerializerOptions(RunRecord.Json) { WriteIndented = true }) + "\n");
        var twoPassPath = Path.Combine(outDirectory, "twopass.jsonl");
        var runsPath = Path.Combine(outDirectory, "runs.jsonl");
        var records = new Dictionary<string, TwoPassRecord>(StringComparer.Ordinal);
        if (resume && File.Exists(twoPassPath))
        {
            foreach (var line in File.ReadLines(twoPassPath).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var record = JsonSerializer.Deserialize<TwoPassRecord>(line, RunRecord.Json)!;
                records[record.Run] = record;
            }

            Console.WriteLine($"Resuming: {records.Count} runs already recorded.");
        }
        else
        {
            File.Delete(twoPassPath);
            File.Delete(runsPath);
        }

        foreach (var folder in new[] { "pdf", "records", "readback", "stamp" })
        {
            Directory.CreateDirectory(Path.Combine(outDirectory, folder));
        }

        var capped = new Dictionary<string, (int Index, string Run)>(StringComparer.Ordinal);
        var cappedFixtures = new Dictionary<string, string>(StringComparer.Ordinal);
        var started = Stopwatch.StartNew();
        await using var twoPass = new StreamWriter(twoPassPath, append: true) { AutoFlush = true, NewLine = "\n" };
        await using var runsFile = new StreamWriter(runsPath, append: true) { AutoFlush = true, NewLine = "\n" };
        foreach (var planned in plan)
        {
            var fixtureKey = FixtureKey(planned);
            if (records.TryGetValue(planned.Run, out var existing))
            {
                NoteCap(existing, planned);
                continue;
            }

            TwoPassRecord record;
            if (planned.NotRunReason is not null)
            {
                record = NotRun(planned, planned.NotRunReason);
            }
            else if (cappedFixtures.TryGetValue(fixtureKey, out var cappedRun)
                || (planned.Series is not null && capped.TryGetValue(planned.Series, out var cap) && planned.SeriesIndex > cap.Index && (cappedRun = cap.Run) is not null))
            {
                record = NotRun(planned, $"after cap at {cappedRun}");
            }
            else if (started.Elapsed > MatrixBudget)
            {
                record = NotRun(planned, "matrix budget");
            }
            else
            {
                var pass1 = await RunChildAsync(planned, 1, readOnlySecondPass);
                var pass2 = pass1.Outcome == "ok"
                    ? await RunChildAsync(planned, 2, readOnlySecondPass)
                    : new PassRecord { Outcome = "not-run", Reason = $"pass 1 {pass1.Outcome}" };
                record = Record(planned, pass1, pass2);
            }

            records[planned.Run] = record;
            await twoPass.WriteLineAsync(JsonSerializer.Serialize(record, RunRecord.Json));
            foreach (var line in ContractLines(record, readOnlySecondPass))
            {
                await runsFile.WriteLineAsync(JsonSerializer.Serialize(line, RunRecord.Json));
            }

            NoteCap(record, planned);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{record.Run}: pass 1 {record.Pass1.Outcome}{Reason(record.Pass1)}, pages~{record.Pass1.Pages}, pdf {record.Pass1.PdfMs:0} ms, chromium peak {record.Pass1.Memory?.ChromiumTreePeakRssKiB / 1024} MiB; "
                + $"pass 2 {record.Pass2.Outcome}{Reason(record.Pass2)}, open {record.Pass2.OpenMs:0} / read {record.Pass2.ReadMs:0} / stamp {record.Pass2.StampMs:0} / save {record.Pass2.SaveMs:0} ms, peak {record.Pass2.HarnessVmHwmKiB / 1024} MiB"));
        }

        // The replaced "stamped" runs: one "not-run" record each, naming the run that replaced it.
        foreach (var r in replaced.Where(r => !records.ContainsKey(r.Run)))
        {
            var record = NotRun(new PlannedRun(r.Run, r.FixtureId, r.Markup, r.Strategy, r.Repetition, null, null, r.Group, null, 0), r.Reason);
            records[r.Run] = record;
            await twoPass.WriteLineAsync(JsonSerializer.Serialize(record, RunRecord.Json));
            foreach (var line in ContractLines(record, readOnlySecondPass))
            {
                await runsFile.WriteLineAsync(JsonSerializer.Serialize(line, RunRecord.Json));
            }
        }

        return [.. plan.Select(p => records[p.Run]), .. replaced.Select(r => records[r.Run])];

        void NoteCap(TwoPassRecord record, PlannedRun planned)
        {
            if (!CapClasses.Contains(record.Pass1.Outcome) && !CapClasses.Contains(record.Pass2.Outcome))
            {
                return;
            }

            cappedFixtures[FixtureKey(planned)] = record.Run;
            if (planned.Series is not null && !capped.ContainsKey(planned.Series))
            {
                capped[planned.Series] = (planned.SeriesIndex, record.Run);
            }
        }
    }

    private static string FixtureKey(PlannedRun planned) => $"{planned.Mutation}/{planned.Fault}/{planned.FixtureId}/{planned.Markup}/{planned.Strategy}";

    private static string Reason(PassRecord pass) => pass.Reason is null ? string.Empty : $" ({pass.Reason})";

    private static TwoPassRecord Record(PlannedRun planned, PassRecord pass1, PassRecord pass2) => new()
    {
        Run = planned.Run,
        FixtureId = planned.FixtureId,
        Markup = planned.Markup,
        Strategy = planned.Strategy,
        Repetition = planned.Repetition,
        Mutation = planned.Mutation,
        Fault = planned.Fault,
        Group = planned.Group,
        Series = planned.Series,
        Pass1 = pass1,
        Pass2 = pass2,
    };

    private static TwoPassRecord NotRun(PlannedRun planned, string reason) => Record(
        planned,
        new PassRecord { Outcome = "not-run", Reason = reason },
        new PassRecord { Outcome = "not-run", Reason = reason });

    /// <summary>
    /// The runs.jsonl lines of a run: its pass-1 PDF in strategy "cell" (no marks,
    /// no footer expected), and its pass-2 PDF in every strategy (marks expected,
    /// the accounting footer). A pass-2 outcome outside the checker's contract
    /// ("guard-failed") is written as "not-run" with the reason.
    /// </summary>
    private static IEnumerable<RunRecord> ContractLines(TwoPassRecord record, bool readOnlySecondPass)
    {
        RunRecord Line(int pass, PassRecord p, string marks, string footer) => new()
        {
            RunId = $"{record.Run}.p{pass}",
            FixtureId = record.FixtureId,
            Markup = record.Markup,
            Repetition = record.Repetition,
            Mutation = record.Mutation,
            Marks = marks,
            Footer = footer,
            Outcome = p.Outcome == "guard-failed" ? "not-run" : p.Outcome,
            Reason = p.Outcome == "guard-failed" ? $"guard-failed: {p.Reason}" : p.Reason,
            Pdf = p.Outcome == "ok" ? p.Pdf : null,
            Dialogs = record.Pass1.Dialogs,
            AbortedRequests = record.Pass1.AbortedRequests,
            Scripts = record.Pass1.Scripts,
            Run = record.Run,
            Pass = pass,
            Strategy = record.Strategy,
            Fault = record.Fault,
        };

        if (record.Strategy == HtmlBuilder.Cell)
        {
            yield return Line(1, record.Pass1, "none", "none");
        }

        if (!readOnlySecondPass)
        {
            yield return Line(2, record.Pass2, "expected", "accounting");
        }
    }

    private async Task<PassRecord> RunChildAsync(PlannedRun planned, int pass, bool readOnly)
    {
        var recordPath = Path.Combine(outDirectory, "records", $"{planned.Run}.p{pass}.json");
        if (File.Exists(recordPath))
        {
            File.Delete(recordPath);
        }

        var oomBefore = OomKills();
        var startedAt = DateTimeOffset.Now;
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TwoPassSpike.dll"));
        var arguments = new List<string>
        {
            pass == 1 ? "layout-one" : "stamp-one", "--fixtures", fixturesDirectory, "--out", outDirectory, "--run", planned.Run,
            "--fixture", planned.FixtureId, "--markup", planned.Markup, "--strategy", planned.Strategy,
            "--mutation", planned.Mutation ?? "-", "--fault", planned.Fault ?? "-", "--font", fontFile,
        };
        if (pass == 2 && readOnly)
        {
            arguments.Add("--read-only");
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

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
}
