namespace TwoPassSpike;

/// <summary>
/// The two probes run before any matrix. They are small matrices of the same
/// driver; their PDFs and readings are judged by the accounting checker's
/// "probe" command (with poppler and qpdf), never here.
/// - Marker probe: B100 and T300u, both markups, strategy "cell"; pass 1, then a
///   read-only reading of the markers (nothing stamped or saved).
/// - Stamp probe: B100 grid, both strategies, both passes.
/// - Stamped bottom-aligned probe: T300u-ab and X64K-ab, grid, strategy "stamped";
///   pass 1, then a read-only reading.
/// - Split-row probe: every shared fixture with a split row, both markups, strategy
///   "cell"; pass 1, then a read-only reading.
/// </summary>
internal static class Probe
{
    public static List<PlannedRun> Markers()
    {
        var runs = new List<PlannedRun>();
        foreach (var fixture in new[] { "B100", "T300u" })
        {
            foreach (var markup in new[] { HtmlBuilder.Table, HtmlBuilder.Grid })
            {
                runs.Add(Planned(fixture, markup, HtmlBuilder.Cell));
            }
        }

        return runs;
    }

    /// <summary>
    /// The split-row rectangle probe: every fixture of the shared series with a split
    /// row, both markups, strategy "cell"; pass 1, then a read-only reading.
    /// </summary>
    public static List<PlannedRun> SplitRows()
    {
        var runs = new List<PlannedRun>();
        foreach (var fixture in new[] { "T150", "T200", "T300", "T500", "T150u", "T200u", "T300u", "T500u", "T300u-ab", "X64K", "X64K-ab" })
        {
            foreach (var markup in new[] { HtmlBuilder.Table, HtmlBuilder.Grid })
            {
                runs.Add(Planned(fixture, markup, HtmlBuilder.Cell));
            }
        }

        return runs;
    }

    /// <summary>The stamp probe runs in grid markup, where the amount may be stamped from the end marker.</summary>
    public static List<PlannedRun> Stamp() =>
    [
        Planned("B100", HtmlBuilder.Grid, HtmlBuilder.Stamped),
        Planned("B100", HtmlBuilder.Grid, HtmlBuilder.Cell),
    ];

    /// <summary>
    /// The bottom-aligned-amount fixtures in grid markup with strategy "stamped" (the amount
    /// cell empty); pass 1, then a read-only reading.
    /// </summary>
    public static List<PlannedRun> StampedAb() =>
    [
        Planned("T300u-ab", HtmlBuilder.Grid, HtmlBuilder.Stamped),
        Planned("X64K-ab", HtmlBuilder.Grid, HtmlBuilder.Stamped),
    ];

    private static PlannedRun Planned(string fixture, string markup, string strategy) =>
        new(MatrixDriver.RunId(fixture, markup, strategy, 1, null), fixture, markup, strategy, 1, null, null, 0, null, 0);
}
