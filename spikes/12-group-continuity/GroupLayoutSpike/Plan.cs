using System.Globalization;
using System.Text.Json;

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
/// The run matrix of the group-continuity experiment, grid markup only. One
/// measured stage of 64 runs. Runs that the grid experiment of
/// spikes/08-pagedjs-pre-split also ran carry its full id after
/// "s1-grid-" (for example "s1-grid-s2-G200-split-t1.00-f0.90-off-r1");
/// runs of fixtures new here carry "s1-grid-new-" and the same id format.
/// The record shape and the members the linked runner and driver of
/// spikes/10-pagedjs-table-strict-accounting use are those of that folder.
/// </summary>
internal static class Plans
{
    public const string Off = "off";
    public const string SplitOn = "split";
    public const string Grid = "grid";
    public const double DefaultTrigger = 1.00;
    public const double DefaultPartLimit = 0.90;

    /// <summary>The group-edge sweep: n = 30 … 66 lines in the one-record group before the next header.</summary>
    public static IEnumerable<int> EdgeLines => Enumerable.Range(30, 37);

    public static string BaseRunId(string fixture, string? mutation, string split, double? trigger, double? partLimit, bool footerOn, int repetition)
    {
        var id = (mutation is null ? string.Empty : mutation + "-") + fixture + "-" + split;
        if (split == SplitOn)
        {
            id += "-t" + trigger!.Value.ToString("0.00##", CultureInfo.InvariantCulture) + "-f" + partLimit!.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        return id + (footerOn ? "-on" : "-off") + "-r" + repetition.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The quick loop (contract test, not a measurement).</summary>
    public static List<PlannedRun> Quick()
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 0, "q-grid-");
        b.Add(string.Empty, "B100", 1, SplitOn, true, 1);
        b.Add(string.Empty, "GE40", 1, SplitOn, true, 3);
        b.Add(string.Empty, "GS1200", 1, SplitOn, true, 5);
        b.Add(string.Empty, "GTL300u", 1, SplitOn, true, 6);
        return runs;
    }

    /// <summary>The measured stage: 64 runs in grid markup, in the order of the plan.</summary>
    public static List<PlannedRun> Stage1()
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 1, "s1-grid-");

        // 1 — controls: positive and HTML-level negative (the grid experiment's first stage).
        b.Add("s1-", "B100", 1, SplitOn, true, 1);
        b.Add("s1-", "K1", 1, SplitOn, true, 1);
        foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
        {
            b.Add("s1-", "B100", 1, SplitOn, true, 1, mutation: mutation);
        }

        b.Add("s1-", "F26K-a", 1, SplitOn, true, 1, mutation: "NC-F");

        // 2 — the ordinary anchor: without the split, with it, and with it without the footer (rule 7).
        b.Add("s1-", "B1000", 3, Off, true, 2);
        b.Add("s1-", "B1000", 3, SplitOn, true, 2);
        b.Add("s1-", "B1000", 1, SplitOn, false, 2);

        // 3 — a group starting near a page end: the one-record group before the next header grows line by line.
        foreach (var n in EdgeLines)
        {
            b.Add("new-", "GE" + n.ToString(CultureInfo.InvariantCulture), 1, SplitOn, true, 3);
        }

        // 4 — two footer-off twins in the sweep (rule 7).
        b.Add("new-", "GE50", 1, SplitOn, false, 4);
        b.Add("new-", "GE60", 1, SplitOn, false, 4);

        // 5 — groups spanning several and many pages.
        b.Add("new-", "GS1200", 1, SplitOn, true, 5);
        b.Add("new-", "GS1200", 1, SplitOn, false, 5);
        b.Add("new-", "GS2400", 1, SplitOn, true, 5);

        // 6 — groups containing over-tall rows (row 50 inside its group; a tall last row; a tall first row).
        b.Add("s2-", "T300u", 1, SplitOn, true, 6, series: "x", seriesIndex: 1);
        b.Add("s2-", "X64K", 1, SplitOn, true, 6, series: "x", seriesIndex: 2);
        b.Add("s2-", "X1M", 1, SplitOn, true, 6, series: "x", seriesIndex: 3);
        b.Add("s2-", "G200", 1, SplitOn, true, 6);
        b.Add("s2-", "G200", 1, SplitOn, false, 6);
        b.Add("new-", "GTL300u", 1, SplitOn, true, 6);
        b.Add("new-", "GTF300u", 1, SplitOn, true, 6);

        // 7 — the baseline exception: the giant-row fixture without the split.
        b.Add("s2-", "G200", 1, Off, true, 7);

        // 8 — the top of the ordinary bracket.
        b.Add("new-", "B4000", 1, SplitOn, true, 8);
        return runs;
    }

    private sealed class Builder(List<PlannedRun> runs, int stage, string prefix)
    {
        public void Add(string tag, string fixture, int repetitions, string split, bool footerOn, int row, string? mutation = null, string? series = null, int seriesIndex = 0)
        {
            double? trigger = split == SplitOn ? DefaultTrigger : null;
            double? partLimit = split == SplitOn ? DefaultPartLimit : null;
            for (var r = 1; r <= repetitions; r++)
            {
                var id = prefix + tag + BaseRunId(fixture, mutation, split, trigger, partLimit, footerOn, r);
                runs.Add(new PlannedRun(id, fixture, Grid, r, mutation, split, trigger, partLimit, footerOn, stage, row, series, seriesIndex));
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
