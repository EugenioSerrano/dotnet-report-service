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
    /// <summary>Every matrix row this run serves (one run can serve two rows of the matrix).</summary>
    public IReadOnlyList<int> MatrixRows => AlsoInRow is { } other ? [MatrixRow, other] : [MatrixRow];

    /// <summary>
    /// The run id without this experiment's stage and markup prefix: for rows 1-12 the id the grid experiment of
    /// spikes/08-pagedjs-pre-split gave the same run, its own stage tag included (three of its runs were planned in both
    /// of its stages with the same values, and run here twice as there); "mem-..." for the memory-series rows.
    /// </summary>
    public string BaseId => RunId[(RunId.IndexOf('-', StringComparison.Ordinal) + 1 + Markup.Length + 1)..];
}

/// <summary>
/// The run matrix. Stage 1 lays out every run of the grid experiment of
/// spikes/08-pagedjs-pre-split, in the same order and with the same values
/// (trigger 1.00 and part limit 0.90 for every split run; the sweeps keep their
/// values), then the four ordinary blocks of the memory series of
/// spikes/02-pdf-layout. Stage 2 is the same list in table markup. Run ids are
/// "s&lt;stage&gt;-&lt;markup&gt;-&lt;the grid experiment's full id&gt;" (for example
/// "s2-table-s1-B100-split-t1.00-f0.90-on-r1"), and "s&lt;stage&gt;-&lt;markup&gt;-mem-..." for the memory series.
/// </summary>
internal static class Plans
{
    public const string Off = "off";
    public const string MeasureOnly = "measure-only";
    public const string SplitOn = "split";
    public const string Grid = "grid";
    public const string Table = "table";
    public const double DefaultTrigger = 1.00;
    public const double DefaultPartLimit = 0.90;

    public static readonly double[] SweepBLimits = [0.50, 0.75, 0.90, 0.95, 0.98, 1.00];

    public static readonly string[] SweepA = ["S080", "S090", "S095", "S098", "S100", "S102", "S105", "S110", "S125"];

    public static readonly string[] FreeText =
    [
        "F8K-b", "F26K-b", "F64K-b", "F256K-b", "F1M-b",
        "F8K-a", "F26K-a", "F64K-a", "F256K-a", "F1M-a",
        "F26K-ba", "F26K-1p-b", "F1M-1p-b", "F26K-1p-a", "F1M-1p-a",
    ];

    /// <summary>The ordinary blocks of the memory series of spikes/02-pdf-layout.</summary>
    public static readonly string[] MemorySeries = ["B250", "B500", "B2000", "B4000"];

    public static string MarkupOfStage(int stage) => stage == 1 ? Grid : Table;

    /// <summary>The id the grid experiment gave a run (its format, without the stage prefix).</summary>
    public static string BaseRunId(string fixture, string? mutation, string split, double? trigger, double? partLimit, bool footerOn, int repetition)
    {
        var id = (mutation is null ? string.Empty : mutation + "-") + fixture + "-" + split;
        if (split == SplitOn)
        {
            id += "-t" + trigger!.Value.ToString("0.00##", CultureInfo.InvariantCulture) + "-f" + partLimit!.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        return id + (footerOn ? "-on" : "-off") + "-r" + repetition.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The quick loop (contract smoke test, not a measurement): seven runs in each markup.</summary>
    public static List<PlannedRun> Quick()
    {
        var runs = new List<PlannedRun>();
        foreach (var markup in new[] { Grid, Table })
        {
            var b = new Builder(runs, 0, markup, "q", string.Empty);
            b.Add("B100", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 1);
            b.Add("K1", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 1);
            b.Add("T300u", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 8);
            b.Add("G200", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 5);
            b.Add("G200", 1, SplitOn, DefaultTrigger, DefaultPartLimit, false, 5);
            b.Add("B100", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 1, mutation: "NC-W");
            b.Add("F26K-b", 1, SplitOn, DefaultTrigger, DefaultPartLimit, true, 12);
        }

        return runs;
    }

    /// <summary>One measured stage: 1 grid, 2 table; the same 95 runs in the same order.</summary>
    public static List<PlannedRun> Stage(int stage)
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, stage, MarkupOfStage(stage), "s" + stage.ToString(CultureInfo.InvariantCulture), "s1-");
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

        // 2 — the ordinary block without and with the split, and one split run without the footer (rule 7).
        b.Add("B1000", 3, Off, null, null, true, 2);
        b.Add("B1000", 3, SplitOn, t, f, true, 2);
        b.Add("B1000", 1, SplitOn, t, f, false, 2);

        // 3 — sweep A: rows near one page, measured, never split (characterisation only).
        foreach (var fixture in SweepA)
        {
            b.Add(fixture, 1, MeasureOnly, null, null, true, 3);
        }

        // 4 — sweep B: part limits on two many-page rows, trigger 1.00 (characterisation only).
        foreach (var fixture in new[] { "T500u", "X64K" })
        {
            foreach (var limit in SweepBLimits)
            {
                b.Add(fixture, 1, SplitOn, t, limit, true, 4);
            }
        }

        // Rows 5-12: the grid experiment's second stage.
        b.Tag = "s2-";

        // 5 — the giant-row fixture with the split, footer on and off; 6 — without the split.
        b.Add("G200", 1, SplitOn, t, f, true, 5);
        b.Add("G200", 1, SplitOn, t, f, false, 5);
        b.Add("G200", 1, Off, null, null, true, 6);

        // 7 — overhead pair; its split run is row 8's T150 run, planned once.
        b.Add("T150", 1, Off, null, null, true, 7);

        // 8 — row series to the record-limit hypothesis. Series for the after-cap rule: T, T-unique, X.
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
        k = 0;
        foreach (var fixture in new[] { "X64K", "X128K", "X256K", "X512K", "X1M" })
        {
            b.Add(fixture, 3, SplitOn, t, f, true, 8, "x", ++k);
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

        // 12 — free text, never split.
        foreach (var fixture in FreeText)
        {
            var dash = fixture.IndexOf('-', StringComparison.Ordinal);
            var size = Array.IndexOf(["F8K", "F26K", "F64K", "F256K", "F1M"], fixture[..dash]) + 1;
            b.Add(fixture, fixture is "F1M-b" or "F1M-a" ? 3 : 1, SplitOn, t, f, true, 12, "free-" + fixture[(dash + 1)..], size);
        }

        // 13 — the ordinary blocks of the memory series of spikes/02-pdf-layout.
        b.Tag = "mem-";
        foreach (var fixture in MemorySeries)
        {
            b.Add(fixture, 1, SplitOn, t, f, true, 13);
        }

        return runs;
    }

    /// <summary>
    /// Compares a stage's plan with the committed plan of the grid experiment: the runs of that plan, without their
    /// stage prefix, must equal this stage's runs of rows 1-12 without theirs, in fixture, mutation, split mode, trigger,
    /// part limit, footer and repetition, and in order; rows 13 are the four memory-series runs.
    /// </summary>
    public static (bool Pass, JsonObject Report) Parity(string referencePlanPath)
    {
        var reference = JsonNode.Parse(File.ReadAllText(referencePlanPath))!["runs"]!.AsArray()
            .Select(n => Key(
                n!["fixtureId"]!.GetValue<string>(),
                n["mutation"]?.GetValue<string>(),
                n["split"]!.GetValue<string>(),
                n["trigger"]?.GetValue<double>(),
                n["partLimit"]?.GetValue<double>(),
                n["footerOn"]!.GetValue<bool>(),
                n["repetition"]!.GetValue<int>()) + " id " + n["runId"]!.GetValue<string>())
            .ToList();
        var report = new JsonObject { ["referenceRuns"] = reference.Count };
        var pass = true;
        foreach (var stage in new[] { 1, 2 })
        {
            var plan = Stage(stage);
            var own = plan.Where(p => p.MatrixRow != 13).Select(p => Key(p.FixtureId, p.Mutation, p.Split, p.Trigger, p.PartLimit, p.FooterOn, p.Repetition) + " id " + p.BaseId).ToList();
            var extra = plan.Where(p => p.MatrixRow == 13).Select(p => p.BaseId).ToList();
            var equal = own.SequenceEqual(reference, StringComparer.Ordinal);
            var memoryOk = extra.SequenceEqual(MemorySeries.Select(m => "mem-" + BaseRunId(m, null, SplitOn, DefaultTrigger, DefaultPartLimit, true, 1)), StringComparer.Ordinal);
            var ids = plan.Select(p => p.RunId).ToList();
            var unique = ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;
            var markupOk = plan.All(p => p.Markup == MarkupOfStage(stage) && p.RunId.StartsWith($"s{stage}-{p.Markup}-", StringComparison.Ordinal));
            var firstDifference = Enumerable.Range(0, Math.Min(own.Count, reference.Count)).FirstOrDefault(i => own[i] != reference[i], -1);
            report[$"stage{stage}"] = new JsonObject
            {
                ["markup"] = MarkupOfStage(stage),
                ["runs"] = plan.Count,
                ["runsAgainstReference"] = own.Count,
                ["equalToReference"] = equal,
                ["firstDifference"] = firstDifference < 0 ? null : $"{own[firstDifference]} | reference {reference[firstDifference]}",
                ["memorySeries"] = memoryOk,
                ["idsUnique"] = unique,
                ["markupAndPrefix"] = markupOk,
            };
            pass &= equal && memoryOk && unique && markupOk && plan.Count == 95;
        }

        var all = Stage(1).Concat(Stage(2)).Select(p => p.RunId).ToList();
        report["idsUniqueAcrossStages"] = all.Distinct(StringComparer.Ordinal).Count() == all.Count;
        pass &= report["idsUniqueAcrossStages"]!.GetValue<bool>();
        report["pass"] = pass;
        return (pass, report);
    }

    private static string Key(string fixture, string? mutation, string split, double? trigger, double? partLimit, bool footerOn, int repetition) =>
        string.Create(CultureInfo.InvariantCulture, $"{fixture}|{mutation}|{split}|{trigger:0.00}|{partLimit:0.00}|{footerOn}|{repetition}");

    private sealed class Builder(List<PlannedRun> runs, int stage, string markup, string prefix, string tag)
    {
        /// <summary>The grid experiment's stage tag of the runs being added.</summary>
        public string Tag { get; set; } = tag;

        public void Add(string fixture, int repetitions, string split, double? trigger, double? partLimit, bool footerOn, int row, string? series = null, int seriesIndex = 0, string? mutation = null, int? alsoInRow = null)
        {
            for (var r = 1; r <= repetitions; r++)
            {
                var id = $"{prefix}-{markup}-{Tag}" + BaseRunId(fixture, mutation, split, trigger, partLimit, footerOn, r);
                runs.Add(new PlannedRun(id, fixture, markup, r, mutation, split, trigger, partLimit, footerOn, stage, row, series, seriesIndex, alsoInRow));
            }
        }
    }

    /// <summary>Serialises a plan for plan.json (the checker's contract plus this harness's fields).</summary>
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
