using System.Globalization;

namespace PreSplitLayoutSpike;

/// <summary>One planned run of the matrix.</summary>
internal sealed record PlannedRun(
    string RunId,
    string FixtureId,
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
    /// <summary>Every matrix row this run serves (one run can serve two rows of the matrix).</summary>
    public IReadOnlyList<int> MatrixRows => AlsoInRow is { } other ? [MatrixRow, other] : [MatrixRow];
}

/// <summary>
/// The run matrix: the quick contract and development loop, stage 1 (controls,
/// the ordinary anchor block, sweep A and sweep B) and stage 2 (everything run
/// with the selected trigger and part limit). Every run uses the grid markup.
/// </summary>
internal static class Plans
{
    public const string Off = "off";
    public const string MeasureOnly = "measure-only";
    public const string SplitOn = "split";
    public const double DefaultTrigger = 1.00;
    public const double DefaultPartLimit = 0.90;

    /// <summary>Part limits of sweep B: the three selectable values and three characterisation values.</summary>
    public static readonly double[] SweepBLimits = [0.50, 0.75, 0.90, 0.95, 0.98, 1.00];

    /// <summary>Sweep A fixtures, in increasing target height.</summary>
    public static readonly string[] SweepA = ["S080", "S090", "S095", "S098", "S100", "S102", "S105", "S110", "S125"];

    public static readonly string[] FreeText =
    [
        "F8K-b", "F26K-b", "F64K-b", "F256K-b", "F1M-b",
        "F8K-a", "F26K-a", "F64K-a", "F256K-a", "F1M-a",
        "F26K-ba", "F26K-1p-b", "F1M-1p-b", "F26K-1p-a", "F1M-1p-a",
    ];

    public static string Format(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    public static string RunId(string fixture, string? mutation, string split, double? trigger, double? partLimit, bool footerOn, int repetition)
    {
        var id = (mutation is null ? string.Empty : mutation + "-") + fixture + "-" + split;
        if (split == SplitOn)
        {
            id += "-t" + trigger!.Value.ToString("0.00##", CultureInfo.InvariantCulture) + "-f" + partLimit!.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        return id + (footerOn ? "-on" : "-off") + "-r" + repetition.ToString(CultureInfo.InvariantCulture);
    }

    public static List<PlannedRun> Quick()
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 0);
        b.Add("B100", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 1);
        b.Add("T300u", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 8, "uniq", 3);
        b.Add("G200", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 5);
        b.Add("G200", 1, SplitOn, DefaultTrigger, DefaultPartLimit, false, 5);
        b.Add("F26K-b", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 12, "free-b", 2);
        b.Add("B100", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 1, mutation: "NC-W");
        return runs;
    }

    /// <summary>Stage 1 — D.2 rows 1-4, with the default trigger 1.00 and part limit 0.90 where the split is on.</summary>
    public static List<PlannedRun> Stage1()
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 1);
        const double t = DefaultTrigger;
        const double f = DefaultPartLimit;

        // 1 — oracle controls: positive and HTML-level negative.
        b.Add("B100", 1, SplitOn, t, f, true, 1);
        b.Add("K1", 1, SplitOn, t, f, true, 1);
        foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
        {
            b.Add("B100", 1, SplitOn, t, f, true, 1, mutation: mutation);
        }

        b.Add("F26K-a", 1, SplitOn, t, f, true, 1, mutation: "NC-F");

        // 2 — the ordinary anchor block without and with the split, and one split run without the footer (rule 7).
        b.Add("B1000", 3, Off, null, null, true, 2);
        b.Add("B1000", 3, SplitOn, t, f, true, 2);
        b.Add("B1000", 1, SplitOn, t, f, false, 2);

        // 3 — sweep A: rows near one page, measured, never split.
        var i = 0;
        foreach (var fixture in SweepA)
        {
            b.Add(fixture, 1, MeasureOnly, null, null, true, 3, "sweep-a", ++i);
        }

        // 4 — sweep B: part limits on two many-page rows, trigger 1.00.
        foreach (var fixture in new[] { "T500u", "X64K" })
        {
            foreach (var limit in SweepBLimits)
            {
                b.Add(fixture, 1, SplitOn, t, limit, true, 4);
            }
        }

        return runs;
    }

    /// <summary>Stage 2 — D.2 rows 5-12 with the selected trigger and part limit.</summary>
    public static List<PlannedRun> Stage2(double t, double f)
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 2);

        // 5 — the giant-row fixture with the split, footer on and off.
        b.Add("G200", 1, SplitOn, t, f, true, 5);
        b.Add("G200", 1, SplitOn, t, f, false, 5);

        // 6 — the giant-row fixture without the split (the behaviour of spikes/02-pdf-layout in this harness).
        b.Add("G200", 1, Off, null, null, true, 6);

        // 7 — overhead pair on a row that fits unsplit in spikes/02-pdf-layout. Its split run is row 8's T150 run,
        // planned once and labelled with both rows.
        b.Add("T150", 1, Off, null, null, true, 7);

        // 8 — row series to the record-limit hypothesis.
        b.Add("K1", 1, SplitOn, t, f, true, 8);
        var k = 0;
        foreach (var fixture in new[] { "T150", "T200", "T300", "T500" })
        {
            b.Add(fixture, 1, SplitOn, t, f, true, 8, "rep", ++k, alsoInRow: fixture == "T150" ? 7 : null);
        }

        k = 0;
        foreach (var fixture in new[] { "T150u", "T200u", "T300u", "T500u" })
        {
            b.Add(fixture, 1, SplitOn, t, f, true, 8, "uniq", ++k);
        }

        b.Add("T500u", 1, SplitOn, t, f, false, 8);
        foreach (var fixture in new[] { "X64K", "X128K", "X256K", "X512K", "X1M" })
        {
            b.Add(fixture, 3, SplitOn, t, f, true, 8, "uniq", ++k);
        }

        // 9 — negative controls inside a split row.
        foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
        {
            b.Add("T300u", 1, SplitOn, t, f, true, 9, mutation: mutation);
        }

        // 10 — two tall cells; 11 — hard line breaks.
        foreach (var fixture in new[] { "W2a", "W2b", "W2c" })
        {
            b.Add(fixture, 1, SplitOn, t, f, true, 10);
        }

        foreach (var fixture in new[] { "H1", "H2", "H3" })
        {
            b.Add(fixture, 1, SplitOn, t, f, true, 11);
        }

        // 12 — free text under the pagination library.
        foreach (var fixture in FreeText)
        {
            var placement = fixture[(fixture.IndexOf('-', StringComparison.Ordinal) + 1)..];
            var size = Array.IndexOf(["F8K", "F26K", "F64K", "F256K", "F1M"], fixture[..fixture.IndexOf('-', StringComparison.Ordinal)]) + 1;
            b.Add(fixture, fixture is "F1M-b" or "F1M-a" ? 3 : 1, SplitOn, t, f, true, 12, "free-" + placement, size);
        }

        return runs;
    }

    private sealed class Builder(List<PlannedRun> runs, int stage)
    {
        public void Add(string fixture, int repetitions, string split, double? trigger, double? partLimit, bool footerOn, int row, string? series = null, int seriesIndex = 0, string? mutation = null, int? alsoInRow = null)
        {
            for (var r = 1; r <= repetitions; r++)
            {
                runs.Add(new PlannedRun(RunId(fixture, mutation, split, trigger, partLimit, footerOn, r), fixture, r, mutation, split, trigger, partLimit, footerOn, stage, row, series, seriesIndex, alsoInRow));
            }
        }
    }
}
