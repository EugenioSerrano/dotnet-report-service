using System.Globalization;
using System.Text.RegularExpressions;
using PageAccountingCheck;
using PaginationFixtures;
using TextIntegrityCheck;

namespace GroupContinuityCheck;

/// <summary>One failure of the group check: rule, class, group, row, page and detail.</summary>
internal sealed record GroupFailure(string Rule, string Class, string? Group, int? Row, int? Page, string Detail)
{
    public string Key => $"{Rule}|{Class}|{Group ?? Row?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
}

/// <summary>Where a group's header and total printed, and the pages of its rows (observations, never pass or fail).</summary>
internal sealed record GroupPlacement(
    string Group,
    bool Closed,
    int Items,
    int HeaderLines,
    List<int> HeaderPages,
    List<int> HeaderAlonePages,
    List<int> HeaderLastLinePages,
    List<int> ItemPages,
    List<int> ItemPagesWithoutHeader,
    int? LastItemPage,
    int? TotalPage,
    string? TotalPlacement);

/// <summary>A page holding a group boundary, a mark or no detail (observations).</summary>
internal sealed record PageObservation(int Page, List<string> GroupsEnding, List<string> GroupsStarting, List<string> MarkGroups, string? PageSubtotal, bool WithoutDetail, bool HeaderOnly);

internal sealed class GroupResult
{
    public List<GroupFailure> Failures { get; set; } = [];

    public List<GroupPlacement> Groups { get; set; } = [];

    public List<PageObservation> Pages { get; set; } = [];

    /// <summary>P-1: header lines with no row of their group on the same page.</summary>
    public int HeadersAlone { get; set; }

    /// <summary>P-2: header lines beyond the first, over all groups.</summary>
    public int HeadersRepeated { get; set; }

    /// <summary>P-2: pages of multi-page groups that hold the group's rows but no header line of it.</summary>
    public int ContinuationPagesWithoutHeader { get; set; }

    /// <summary>P-3: totals not on the page of their group's last row.</summary>
    public int TotalsSeparated { get; set; }

    /// <summary>The fixture's own group-end totals that differ from the computed sums (a fixture defect when non-empty).</summary>
    public List<string> FixtureTotalDefects { get; set; } = [];
}

/// <summary>
/// The group check. Expected values come only from the source fixture: its groups,
/// their captions, their rows and the rows' amounts, in order. The printed side is
/// the PDF reading of spikes/09-two-pass-page-subtotals (content lines in reading
/// order, each item row's lines) — a header line is a content line whose first word
/// is a group id; a total line is one whose first two words are "Group total"; the
/// block-total line starts with "Block total". The group-total text is written
/// into the page from the fixture, so this check shows whether that static text
/// survives pagination; the page subtotals and the chain, computed by the
/// accounting script after layout, are judged by the accounting rules.
/// </summary>
internal static partial class GroupCheck
{
    public static GroupResult Check(PdfReading reading, Fixture fixture)
    {
        var result = new GroupResult();
        var groups = SourceGroups.Of(fixture);
        var groupOfRow = new Dictionary<int, SourceGroup>();
        foreach (var g in groups)
        {
            foreach (var item in g.Items)
            {
                groupOfRow[item.Sequence!.Value] = g;
            }

            if (g.Closed && TryValue(g.FixtureTotal) != g.ComputedTotal)
            {
                result.FixtureTotalDefects.Add($"{g.Id}: the fixture prints {g.FixtureTotal}, its rows sum to {Format(g.ComputedTotal)}");
            }
        }

        var lines = reading.Document.ContentLines;
        var headers = new List<(int Line, string Id, string Text)>();
        var totals = new List<(int Line, string? Group, string Amount)>();
        int? blockLine = null;
        string? blockAmount = null;
        string? lastItemGroup = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (reading.RowOf[i] is { } seq)
            {
                lastItemGroup = groupOfRow.TryGetValue(seq, out var g) ? g.Id : null;
                continue;
            }

            var words = lines[i].Words;
            if (words.Count == 0)
            {
                continue;
            }

            if (GroupId().IsMatch(words[0].Text) && reading.Kinds[i] is LineKind.Group)
            {
                headers.Add((i, words[0].Text, lines[i].Text));
            }
            else if (words.Count >= 2 && words[0].Text == "Group" && words[1].Text == "total" && reading.Kinds[i] is LineKind.Group)
            {
                totals.Add((i, lastItemGroup, words[^1].Text));
            }
            else if (words.Count >= 2 && words[0].Text == "Block" && words[1].Text == "total")
            {
                blockLine = i;
                blockAmount = words[^1].Text;
            }
        }

        // GC-1 headers.
        var ids = groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var mine = headers.Where(h => h.Id == g.Id).ToList();
            if (mine.Count == 0)
            {
                result.Failures.Add(new GroupFailure("GC-1", "header-missing", g.Id, null, null, $"no header line for {g.Caption}"));
            }
            else if (mine.Any(h => TextNormalisation.N(h.Text) != TextNormalisation.N(g.Caption)))
            {
                var bad = mine.First(h => TextNormalisation.N(h.Text) != TextNormalisation.N(g.Caption));
                result.Failures.Add(new GroupFailure("GC-1", "header-text", g.Id, null, lines[bad.Line].Page, $"printed '{bad.Text}', expected '{g.Caption}'"));
            }
        }

        foreach (var h in headers.Where(h => !ids.Contains(h.Id)))
        {
            result.Failures.Add(new GroupFailure("GC-1", "header-unknown", h.Id, null, lines[h.Line].Page, $"'{h.Text}'"));
        }

        var printedOrder = headers.Where(h => ids.Contains(h.Id)).Select(h => h.Id).Distinct(StringComparer.Ordinal).ToList();
        var sourceOrder = groups.Select(g => g.Id).Where(printedOrder.Contains).ToList();
        if (!printedOrder.SequenceEqual(sourceOrder, StringComparer.Ordinal))
        {
            result.Failures.Add(new GroupFailure("GC-1", "header-order", null, null, null, $"printed {string.Join(",", printedOrder)}, source {string.Join(",", sourceOrder)}"));
        }

        // GC-2 group subtotals (a total line belongs to the group of the row printed just before it).
        foreach (var t in totals.Where(t => t.Group is null))
        {
            result.Failures.Add(new GroupFailure("GC-2", "total-unexpected", null, null, lines[t.Line].Page, "a total line before any row"));
        }

        foreach (var g in groups)
        {
            var mine = totals.Where(t => t.Group == g.Id).ToList();
            if (!g.Closed)
            {
                if (mine.Count > 0)
                {
                    result.Failures.Add(new GroupFailure("GC-2", "total-unexpected", g.Id, null, lines[mine[0].Line].Page, $"the group is open at the block end but a total '{mine[0].Amount}' prints"));
                }

                continue;
            }

            if (mine.Count == 0)
            {
                result.Failures.Add(new GroupFailure("GC-2", "total-missing", g.Id, null, null, $"expected {Format(g.ComputedTotal)}"));
                continue;
            }

            if (mine.Count > 1)
            {
                result.Failures.Add(new GroupFailure("GC-2", "total-duplicated", g.Id, null, lines[mine[1].Line].Page, $"{mine.Count} total lines"));
            }

            if (TryValue(mine[0].Amount) != g.ComputedTotal)
            {
                result.Failures.Add(new GroupFailure("GC-2", "total-wrong", g.Id, null, lines[mine[0].Line].Page, $"printed {mine[0].Amount}, expected {Format(g.ComputedTotal)}"));
            }
        }

        // GC-3 membership: for every group whose header printed, its rows lie after its first header and before its
        // first total, and no row of another group lies between them.
        foreach (var g in groups)
        {
            var header = headers.FirstOrDefault(h => h.Id == g.Id);
            if (header.Text is null)
            {
                continue;
            }

            var total = totals.FirstOrDefault(t => t.Group == g.Id);
            var end = total.Amount is null ? int.MaxValue : total.Line;
            foreach (var item in g.Items)
            {
                var seq = item.Sequence!.Value;
                if (reading.AnchorLine.TryGetValue(seq, out var anchor) && (anchor < header.Line || anchor > end))
                {
                    result.Failures.Add(new GroupFailure("GC-3", "row-outside-group", g.Id, seq, lines[anchor].Page, $"row {seq} at line {anchor}, group lines {header.Line}-{(end == int.MaxValue ? "end" : end.ToString(CultureInfo.InvariantCulture))}"));
                }
            }

            foreach (var (seq, anchor) in reading.AnchorLine)
            {
                if (anchor > header.Line && anchor < end && groupOfRow.TryGetValue(seq, out var other) && other.Id != g.Id)
                {
                    result.Failures.Add(new GroupFailure("GC-3", "row-outside-group", other.Id, seq, lines[anchor].Page, $"row {seq} of {other.Id} inside {g.Id}"));
                }
            }
        }

        // GC-4 block total.
        var expectedBlock = fixture.Items.Sum(i => Parse(i.Amount!));
        if (blockLine is null || TryValue(blockAmount) != expectedBlock)
        {
            result.Failures.Add(new GroupFailure("GC-4", "block-total-wrong", null, null, blockLine is null ? null : lines[blockLine.Value].Page, $"printed {blockAmount ?? "none"}, expected {Format(expectedBlock)}"));
        }

        // Observations P-1 … P-5.
        foreach (var g in groups)
        {
            var rows = g.Items.Select(i => i.Sequence!.Value).Where(reading.RowPages.ContainsKey).ToList();
            var itemPages = rows.SelectMany(s => reading.RowPages[s]).Distinct().Order().ToList();
            var mine = headers.Where(h => h.Id == g.Id).ToList();
            var headerPages = mine.Select(h => lines[h.Line].Page).ToList();
            var alone = mine.Where(h => !itemPages.Contains(lines[h.Line].Page)).Select(h => lines[h.Line].Page).ToList();
            var lastLine = mine.Where(h => !lines.Skip(h.Line + 1).Any(l => l.Page == lines[h.Line].Page)).Select(h => lines[h.Line].Page).ToList();
            int? lastItemPage = rows.Count == 0 ? null : lines[reading.LastLine[rows[^1]]].Page;
            var total = totals.FirstOrDefault(t => t.Group == g.Id);
            int? totalPage = total.Amount is null ? null : lines[total.Line].Page;
            string? placement = null;
            if (totalPage is { } tp && lastItemPage is { } lp)
            {
                var firstOnPage = lines.FindIndex(l => l.Page == tp) == total.Line;
                placement = tp == lp ? "same page as the last row" : firstOnPage ? "next page, first content line" : "next page, after other lines";
                if (tp != lp)
                {
                    result.TotalsSeparated++;
                }
            }

            var withoutHeader = itemPages.Count > 1 ? itemPages.Where(p => !headerPages.Contains(p)).ToList() : [];
            result.HeadersAlone += alone.Count;
            result.HeadersRepeated += Math.Max(0, mine.Count - 1);
            result.ContinuationPagesWithoutHeader += withoutHeader.Count;
            result.Groups.Add(new GroupPlacement(g.Id, g.Closed, g.Items.Count, mine.Count, headerPages, alone, lastLine, itemPages, withoutHeader, lastItemPage, totalPage, placement));
        }

        for (var page = 1; page <= reading.Pages; page++)
        {
            var ending = totals.Where(t => lines[t.Line].Page == page && t.Group is not null).Select(t => t.Group!).ToList();
            var starting = headers.Where(h => lines[h.Line].Page == page).Select(h => h.Id).ToList();
            var marks = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Page == page && lines[i].Words.Any(w => w.Text == PdfReading.Continues))
                {
                    marks.Add(reading.RowOf[i] is { } s && groupOfRow.TryGetValue(s, out var mg) ? $"{mg.Id} row {s}" : "unassigned");
                }
            }

            var withoutDetail = page <= reading.WithoutDetail.Length && reading.WithoutDetail[page - 1];
            var headerOnly = page <= reading.HeaderOnly.Length && reading.HeaderOnly[page - 1];
            if (ending.Count > 0 || starting.Count > 0 || marks.Count > 0 || withoutDetail)
            {
                result.Pages.Add(new PageObservation(page, ending, starting, marks, reading.FooterThirds(page).Centre, withoutDetail, headerOnly));
            }
        }

        return result;
    }

    public static decimal? TryValue(string? text) =>
        text is not null && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static decimal Parse(string text) => decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    public static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^G\d{4}$")]
    private static partial Regex GroupId();
}
