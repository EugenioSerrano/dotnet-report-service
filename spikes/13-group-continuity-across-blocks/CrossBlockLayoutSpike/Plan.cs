using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PreSplitLayoutSpike;

/// <summary>One planned run of the matrix. <see cref="RunId"/> carries the stage and the markup from the start.</summary>
internal sealed record PlannedRun(
    string RunId,
    string FixtureId,
    string Markup,
    int Repetition,
    string? Mutation,
    string Split,
    double? Trigger,
    double? PartLimit,
    bool FooterOn,
    int Stage,
    int MatrixRow,
    string? Series,
    int SeriesIndex,
    int? AlsoInRow = null)
{
    /// <summary>Every matrix row this run serves.</summary>
    public IReadOnlyList<int> MatrixRows => AlsoInRow is { } other ? [MatrixRow, other] : [MatrixRow];
}

/// <summary>
/// The run matrix of the group-continuity-across-blocks experiment, grid markup
/// only. A report is cut into parts of a fixed number of records; each part is one
/// fixture ("&lt;report&gt;-P&lt;k&gt;") laid out as its own run. The parts of every report
/// are read from reports.json in the fixture folder (written by the fixture
/// builder and pinned by the folder's SHA256SUMS). Runs that
/// spikes/12-group-continuity also ran keep that folder's exact ids; runs of new
/// fixtures carry "s1-grid-new-". The record shape and the members the linked
/// runner and driver of spikes/10-pagedjs-table-strict-accounting use are that
/// folder's.
/// </summary>
internal static class Plans
{
    public const string Off = "off";
    public const string SplitOn = "split";
    public const string Grid = "grid";
    public const double DefaultTrigger = 1.00;
    public const double DefaultPartLimit = 0.90;
    public const string ReportsFileName = "reports.json";

    public static string BaseRunId(string fixture, string? mutation, string split, double? trigger, double? partLimit, bool footerOn, int repetition)
    {
        var id = (mutation is null ? string.Empty : mutation + "-") + fixture + "-" + split;
        if (split == SplitOn)
        {
            id += "-t" + trigger!.Value.ToString("0.00##", CultureInfo.InvariantCulture) + "-f" + partLimit!.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        return id + (footerOn ? "-on" : "-off") + "-r" + repetition.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The part fixtures of a report, in order, from reports.json.</summary>
    public static List<string> PartsOf(string fixturesDirectory, string report)
    {
        var reports = JsonNode.Parse(File.ReadAllText(Path.Combine(fixturesDirectory, ReportsFileName)))!["reports"]!.AsArray();
        var node = reports.FirstOrDefault(r => r!["report"]!.GetValue<string>() == report)
            ?? throw new InvalidDataException($"{ReportsFileName} has no report {report}.");
        return [.. node["parts"]!.AsArray().Select(p => p!["fixture"]!.GetValue<string>())];
    }

    /// <summary>The quick loop (contract test, not a measurement): the hand-over probe, B100 and three reports.</summary>
    public static List<PlannedRun> Quick(string fixtures)
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 0, "q-grid-");
        b.Report(fixtures, "XB-PROBE", 1, 1);
        b.Add(string.Empty, "B100", 1, SplitOn, true, 1);
        b.Report(fixtures, "XB-L183-H1", 1, 4);
        b.Report(fixtures, "XB-L180-H1", 1, 4);
        b.Report(fixtures, "XB-TL150-H1", 1, 6);
        return runs;
    }

    /// <summary>The calibration of the page-coinciding boundary (not a measurement): part 1 of records 594 … 594 + L − 1, L = 240 … 261 (or 220 … 281).</summary>
    public static List<PlannedRun> Calibration(string fixtures)
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 0, "c-grid-");
        foreach (var file in Directory.GetFiles(fixtures, "XB-CAL-L*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal))
        {
            b.Add("new-", file!, 1, SplitOn, true, 0);
        }

        return runs;
    }

    /// <summary>The measured stage: 97 runs in grid markup, in the order of the plan.</summary>
    public static List<PlannedRun> Stage1(string fixtures)
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 1, "s1-grid-");

        // 1 — text controls: positive and HTML-level negative (the runs of spikes/12-group-continuity, same ids).
        b.Add("s1-", "B100", 1, SplitOn, true, 1);
        b.Add("s1-", "K1", 1, SplitOn, true, 1);
        foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
        {
            b.Add("s1-", "B100", 1, SplitOn, true, 1, mutation: mutation);
        }

        b.Add("s1-", "F26K-a", 1, SplitOn, true, 1, mutation: "NC-F");

        // 2 — the footer-off base of the text extraction controls.
        b.Add("s2-", "G200", 1, SplitOn, false, 2);

        // 3 — parity with spikes/12-group-continuity (no opening attribute).
        b.Add("s1-", "B1000", 1, SplitOn, true, 3);
        foreach (var id in new[] { "GE52", "GE61", "GS1200", "GTF300u", "GTL300u" })
        {
            b.Add("new-", id, 1, SplitOn, true, 3);
        }

        // 4 — the boundary sweep: near a group's end, between groups, near its start.
        foreach (var l in new[] { 180, 181, 182, 183, 184, 185 })
        {
            b.Report(fixtures, "XB-L" + l.ToString(CultureInfo.InvariantCulture) + "-H1", 1, 4);
        }

        // 5 — a one-record group closing a part.
        b.Report(fixtures, "XB-L201-H1", 1, 5);

        // 6 — over-tall rows at the boundary.
        b.Report(fixtures, "XB-TL150-H1", 1, 6);
        b.Report(fixtures, "XB-TF150-H1", 1, 6);

        // 7 — groups spanning whole blocks.
        b.Report(fixtures, "XB-W250-H1", 1, 7);
        b.Report(fixtures, "XB-G500-H1", 1, 7);

        // 8 — a block boundary on a page boundary (calibrated block limit).
        b.Report(fixtures, "XB-PB-H1", 1, 8);

        // 9 — the baseline: independently laid-out parts.
        b.Report(fixtures, "XB-L185-H0", 1, 9);
        b.Report(fixtures, "XB-W250-H0", 1, 9);

        // 10 — reproducibility: two more repetitions of the whole-block report.
        b.Report(fixtures, "XB-W250-H1", 2, 10);
        b.Report(fixtures, "XB-W250-H1", 3, 10);

        // 11 — the top of the bracket: the whole dataset in ten parts.
        b.Report(fixtures, "XB-ALL-H1", 1, 11);
        return runs;
    }

    private sealed class Builder(List<PlannedRun> runs, int stage, string prefix)
    {
        public void Add(string tag, string fixture, int repetitions, string split, bool footerOn, int row, string? mutation = null, int firstRepetition = 1)
        {
            double? trigger = split == SplitOn ? DefaultTrigger : null;
            double? partLimit = split == SplitOn ? DefaultPartLimit : null;
            for (var r = firstRepetition; r < firstRepetition + repetitions; r++)
            {
                var id = prefix + tag + BaseRunId(fixture, mutation, split, trigger, partLimit, footerOn, r);
                if (runs.Any(p => p.RunId == id))
                {
                    throw new InvalidOperationException($"duplicate run id {id}");
                }

                runs.Add(new PlannedRun(id, fixture, Grid, r, mutation, split, trigger, partLimit, footerOn, stage, row, null, 0));
            }
        }

        /// <summary>Every part of a report, in order, as one repetition.</summary>
        public void Report(string fixtures, string report, int repetition, int row)
        {
            foreach (var part in PartsOf(fixtures, report))
            {
                Add("new-", part, 1, SplitOn, true, row, firstRepetition: repetition);
            }
        }
    }

    /// <summary>Serialises a plan for plan.json (the checkers' contract plus this harness's fields).</summary>
    public static string ToJson(string matrix, IEnumerable<PlannedRun> plan) => JsonSerializer.Serialize(new
    {
        matrix,
        runs = plan.Select(p => new
        {
            p.RunId,
            p.FixtureId,
            p.Markup,
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
    }, RunRecord.JsonIndented) + "\n";
}
