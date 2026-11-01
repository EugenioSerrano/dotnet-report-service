using System.Globalization;
using System.Text.Json;

namespace PreSplitLayoutSpike;

/// <summary>
/// One planned run of the matrix. <see cref="RunId"/> carries the stage and the configuration from the start.
/// <see cref="Configuration"/> names the group behaviours the PDF page attempts; <see cref="Anchor"/> is the id
/// of the same run in spikes/12-group-continuity, when that experiment ran it.
/// </summary>
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
    int? AlsoInRow = null,
    string Configuration = "none",
    string? Anchor = null)
{
    /// <summary>Every matrix row this run serves.</summary>
    public IReadOnlyList<int> MatrixRows => AlsoInRow is { } other ? [MatrixRow, other] : [MatrixRow];
}

/// <summary>
/// The run matrix of the group-rules experiment, grid markup only, every run split at
/// trigger 1.00 and part limit 0.90. Five configurations of the same blocks: none
/// (the PDF page byte for byte as in spikes/12-group-continuity), each group
/// behaviour alone, and all three. One measured stage of 305 runs. The record shape
/// and the members the linked driver of spikes/10-pagedjs-table-strict-accounting
/// uses are those of that folder.
/// </summary>
internal static class Plans
{
    public const string Off = "off";
    public const string SplitOn = "split";
    public const string Grid = "grid";
    public const double DefaultTrigger = 1.00;
    public const double DefaultPartLimit = 0.90;

    public const string None = "none";
    public const string KeepHeader = "keep-header";
    public const string RepeatHeader = "repeat-header";
    public const string KeepTotal = "keep-total";
    public const string AllThree = "all-three";

    /// <summary>The five configurations, in the order every fixture runs them.</summary>
    public static readonly string[] Configurations = [None, KeepHeader, RepeatHeader, KeepTotal, AllThree];

    /// <summary>The group rules a configuration attempts (the names the page script reads).</summary>
    public static IReadOnlyList<string> Rules(string configuration) => configuration switch
    {
        None => [],
        KeepHeader => [KeepHeader],
        RepeatHeader => [RepeatHeader],
        KeepTotal => [KeepTotal],
        AllThree => [KeepHeader, RepeatHeader, KeepTotal],
        _ => throw new ArgumentException($"Unknown configuration '{configuration}'.", nameof(configuration)),
    };

    /// <summary>The group-edge sweep: n = 30 … 66 lines in the one-record group before the next header.</summary>
    public static IEnumerable<int> EdgeLines => Enumerable.Range(30, 37);

    /// <summary>The case set of every configuration, in run order.</summary>
    public static IEnumerable<string> CaseSet =>
        EdgeLines.Select(n => "GE" + n.ToString(CultureInfo.InvariantCulture))
            .Concat(["GS1200", "GS2400", "GTL300u", "GTF300u", "T300u", "X64K", "X1M", "G200", "B100", "B1000", "B4000"]);

    /// <summary>The fixtures repeated three times for the cost comparison.</summary>
    public static readonly string[] CostFixtures = ["B1000", "GS2400", "X1M", "B4000"];

    /// <summary>The fixtures with a footer-off twin (rule 7).</summary>
    public static readonly string[] TwinFixtures = ["GE50", "GE60", "GS1200", "G200"];

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
        var b = new Builder(runs, 0, "q-");
        b.Add(None, "B100", 1, true, 1);
        b.Add(AllThree, "B100", 1, true, 1);
        b.Add(None, "B1000", 1, true, 1);
        b.Add(None, "GE52", 1, true, 2);
        b.Add(AllThree, "GE52", 1, true, 2);
        b.Add(KeepHeader, "GTF300u", 1, true, 2);
        b.Add(KeepTotal, "GE61", 1, true, 2);
        return runs;
    }

    /// <summary>The mechanism probe (not a measurement): is the mechanism expressed as written.</summary>
    public static List<PlannedRun> Probe()
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 0, "p-");
        b.Add(AllThree, "GS1200", 1, true, 1);
        b.Add(AllThree, "GE52", 1, true, 1);
        return runs;
    }

    /// <summary>The measured stage: 305 runs in grid markup, in the order of the plan.</summary>
    public static List<PlannedRun> Stage1()
    {
        var runs = new List<PlannedRun>();
        var b = new Builder(runs, 1, "s1-");

        // 1 — controls, without any group behaviour: positive and HTML-level negative.
        b.Add(None, "B100", 1, true, 1);
        b.Add(None, "K1", 1, true, 1);
        foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
        {
            b.Add(None, "B100", 1, true, 1, mutation: mutation);
        }

        b.Add(None, "F26K-a", 1, true, 1, mutation: "NC-F");

        // 2 — the case set in the five configurations, interleaved per fixture (B100 without a behaviour is row 1's run).
        foreach (var fixture in CaseSet)
        {
            foreach (var configuration in Configurations)
            {
                if (fixture == "B100" && configuration == None)
                {
                    continue;
                }

                b.Add(configuration, fixture, 1, true, 2);
            }
        }

        // 3 — footer-off twins (rule 7), in the five configurations.
        foreach (var fixture in TwinFixtures)
        {
            foreach (var configuration in Configurations)
            {
                b.Add(configuration, fixture, 1, false, 3);
            }
        }

        // 4 — repetitions 2 and 3 for the cost comparison, interleaved as row 2.
        foreach (var fixture in CostFixtures)
        {
            foreach (var configuration in Configurations)
            {
                b.Add(configuration, fixture, 3, true, 4, firstRepetition: 2);
            }
        }

        return runs;
    }

    /// <summary>
    /// The id of the same run in spikes/12-group-continuity, for a run without a group behaviour
    /// that experiment also ran; null otherwise.
    /// </summary>
    public static string? AnchorOf(string configuration, string fixture, string? mutation, bool footerOn, int repetition)
    {
        if (configuration != None)
        {
            return null;
        }

        var baseId = BaseRunId(fixture, mutation, SplitOn, DefaultTrigger, DefaultPartLimit, footerOn, repetition);
        string? tag = fixture switch
        {
            "B100" or "K1" or "F26K-a" when footerOn && repetition == 1 => "s1-",
            "B1000" when footerOn => "s1-",
            "T300u" or "X64K" or "X1M" when footerOn && repetition == 1 => "s2-",
            "G200" when repetition == 1 => "s2-",
            "GS1200" or "GE50" or "GE60" when repetition == 1 => "new-",
            _ when fixture.StartsWith("GE", StringComparison.Ordinal) && footerOn && repetition == 1 => "new-",
            "GS2400" or "GTL300u" or "GTF300u" or "B4000" when footerOn && repetition == 1 => "new-",
            _ => null,
        };
        return tag is null ? null : "s1-grid-" + tag + baseId;
    }

    private sealed class Builder(List<PlannedRun> runs, int stage, string prefix)
    {
        public void Add(string configuration, string fixture, int lastRepetition, bool footerOn, int row, string? mutation = null, int firstRepetition = 1)
        {
            string? series = fixture switch
            {
                "T300u" or "X64K" or "X1M" => "x-" + configuration,
                _ => null,
            };
            var seriesIndex = fixture switch
            {
                "T300u" => 1,
                "X64K" => 2,
                "X1M" => 3,
                _ => 0,
            };
            for (var r = firstRepetition; r <= lastRepetition; r++)
            {
                var id = prefix + configuration + "-" + BaseRunId(fixture, mutation, SplitOn, DefaultTrigger, DefaultPartLimit, footerOn, r);
                var anchor = stage == 1 ? AnchorOf(configuration, fixture, mutation, footerOn, r) : null;
                runs.Add(new PlannedRun(id, fixture, Grid, r, mutation, SplitOn, DefaultTrigger, DefaultPartLimit, footerOn, stage, row, series, seriesIndex,
                    Configuration: configuration, Anchor: anchor));
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
            p.Configuration,
            p.Anchor,
        }),
    }, RunRecord.JsonIndented) + "\n";
}
