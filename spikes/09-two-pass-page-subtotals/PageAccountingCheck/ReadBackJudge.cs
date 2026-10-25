using System.Globalization;
using PaginationFixtures;

namespace PageAccountingCheck;

/// <summary>The judgement of one read-back: its failures and how many rows were judged.</summary>
internal sealed class ReadBackResult
{
    public List<CheckFailure> Failures { get; } = [];

    public int RowsExpected { get; set; }

    public int RowsReported { get; set; }

    public int RowsJudged { get; set; }

    /// <summary>RB-a … RB-c held for every row.</summary>
    public bool Reliable => Failures.All(f => f.Check == "RB-d");
}

/// <summary>
/// Judges the second pass's reported reading against this checker's own reading
/// of the pass-1 PDF (the layout the markers came from) — never the other way round:
/// - RB-a every row: the reported sequences are the fixture's item sequences, each once
///   (readback-missing, readback-extra, readback-unknown);
/// - RB-b correct page: start page = the page of the row's anchor line; end page = the
///   last page on which the row has a word (readback-page);
/// - RB-c correct order: the rows reported and expected, sorted by (start page, start
///   rectangle top), are in the fixture's order (readback-order);
/// - RB-d position: the start rectangle holds the centre of the row's sequence-number
///   box and the end rectangle the centre of the box of the word the end marker wraps,
///   within 1 pt (readback-position). The words are the checker's own, wherever it
///   reads them, so a wrong reported page does not also fail RB-d.
/// </summary>
internal static class ReadBackJudge
{
    public const double PositionTolerancePt = 1.0;

    public static ReadBackResult Judge(ReadBackFile reported, PdfReading pass1, Fixture fixture)
    {
        var result = new ReadBackResult();
        var expected = fixture.Items.Select(r => r.Sequence!.Value).ToList();
        var expectedSet = expected.ToHashSet();
        result.RowsExpected = expected.Count;
        result.RowsReported = reported.Rows.Count;
        var counts = reported.Rows.GroupBy(r => r.Seq).ToDictionary(g => g.Key, g => g.Count());

        // RB-a.
        foreach (var seq in expected.Where(s => !counts.ContainsKey(s)))
        {
            result.Failures.Add(new CheckFailure("RB-a", "readback-missing", null, $"row {seq}"));
        }

        foreach (var (seq, count) in counts.Where(kv => kv.Value > 1))
        {
            result.Failures.Add(new CheckFailure("RB-a", "readback-extra", null, $"row {seq} reported {count} times"));
        }

        foreach (var seq in counts.Keys.Where(s => !expectedSet.Contains(s)))
        {
            result.Failures.Add(new CheckFailure("RB-a", "readback-unknown", null, $"row {seq} is not in the block"));
        }

        var judged = reported.Rows.Where(r => expectedSet.Contains(r.Seq) && counts[r.Seq] == 1).ToList();
        result.RowsJudged = judged.Count;

        // RB-b.
        foreach (var row in judged)
        {
            if (!pass1.RowPages.TryGetValue(row.Seq, out var pages))
            {
                result.Failures.Add(new CheckFailure("RB-b", "readback-page", row.StartPage, $"row {row.Seq}: the checker reads no word of it"));
                continue;
            }

            var start = pass1.AnchorPage[row.Seq];
            var end = pages.Max;
            if (row.StartPage != start || row.EndPage != end)
            {
                result.Failures.Add(new CheckFailure("RB-b", "readback-page", row.StartPage != start ? row.StartPage : row.EndPage,
                    $"row {row.Seq}: reported pages {row.StartPage}–{row.EndPage}, read {start}–{end}"));
            }
        }

        // RB-c.
        var order = judged
            .OrderBy(r => r.StartPage)
            .ThenBy(r => r.StartRect?.Y0 ?? double.MaxValue)
            .ThenBy(r => r.StartRect?.X0 ?? double.MaxValue)
            .Select(r => r.Seq)
            .ToList();
        var judgedSet = order.ToHashSet();
        var expectedOrder = expected.Where(judgedSet.Contains).ToList();
        if (!order.SequenceEqual(expectedOrder))
        {
            var at = Enumerable.Range(0, order.Count).First(i => order[i] != expectedOrder[i]);
            result.Failures.Add(new CheckFailure("RB-c", "readback-order", null, $"position {at + 1}: row {order[at]} where the block has row {expectedOrder[at]}"));
        }

        // RB-d.
        foreach (var row in judged)
        {
            if (pass1.SeqWord.TryGetValue(row.Seq, out var seqWord)
                && (row.StartRect is null || !row.StartRect.Contains(seqWord.XCentre, seqWord.YCentre, PositionTolerancePt)))
            {
                result.Failures.Add(new CheckFailure("RB-d", "readback-position", row.StartPage, $"row {row.Seq}: the start rectangle does not hold the sequence number's centre {Point(seqWord.XCentre, seqWord.YCentre)}"));
            }

            if (pass1.EndWord(row.Seq) is { } endWord
                && (row.EndRect is null || !row.EndRect.Contains(endWord.XCentre, endWord.YCentre, PositionTolerancePt)))
            {
                result.Failures.Add(new CheckFailure("RB-d", "readback-position", row.EndRectPage, $"row {row.Seq}: the end rectangle does not hold the centre {Point(endWord.XCentre, endWord.YCentre)} of '{endWord.Text}'"));
            }
        }

        return result;
    }

    private static string Point(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"({x:0.##}, {y:0.##})");
}
