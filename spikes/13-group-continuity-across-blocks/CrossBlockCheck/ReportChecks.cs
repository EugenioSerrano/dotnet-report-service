using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PageAccountingCheck;
using PaginationFixtures;
using PdfLayoutSpike;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace GroupContinuityCheck;

/// <summary>The report-level results of one instance: the accounting rules across parts, the group rules, and the chain at every boundary.</summary>
internal sealed class ReportResult
{
    public List<RuleLine> Rules { get; set; } = [];

    /// <summary>XA failures as "XA-n|p&lt;global page&gt;" ("XA-1|p" when the rule names no page).</summary>
    public List<string> AccountingFailures { get; set; } = [];

    public List<GroupFailure> GroupFailures { get; set; } = [];

    /// <summary>PB-7: one entry per boundary, "continuous" or "broken", with the two amounts.</summary>
    public List<BoundaryChain> Boundaries { get; set; } = [];

    public int Pages { get; set; }

    /// <summary>Every failure and every broken boundary, as the controls compare them.</summary>
    public List<string> Keys() =>
        [.. AccountingFailures, .. GroupFailures.Select(f => f.Key).Distinct(), .. Boundaries.Where(b => b.State == "broken").Select(b => $"PB-7|broken|before part {b.BeforePart}")];
}

internal sealed record BoundaryChain(int BeforePart, int Page, string? CarriedBefore, string? BroughtAfter, string State);

/// <summary>
/// The checks of an assembled report. XA: the rules of spikes/02-pdf-layout (rows,
/// end-page subtotals, chain, visible amounts, marks), unchanged, applied to the
/// report: the slices' readings concatenated in global page order, the parts' blocks
/// concatenated in part order (the chain rule starts at 0.00 on the report's first page
/// and stops at its first failing page). RG: headers, group totals, membership and part
/// totals across the parts, every expected value from the source fixtures' items and
/// group-start rows. PB-7: the chain at every boundary, read from the footers.
/// </summary>
internal static partial class ReportChecks
{
    private const string Cont = "(cont.)";

    public static ReportResult Check(string id, IReadOnlyList<PartDoc> parts)
    {
        var readings = parts.Select(p => p.Read()).ToList();
        var result = new ReportResult { Pages = readings.Sum(r => r.Pages) };

        // XA — the linked rules on the concatenated report.
        var layout = new LayoutResult { FooterOn = true };
        var offset = 0;
        foreach (var reading in readings)
        {
            var l = reading.ToLayout();
            foreach (var page in l.Pages)
            {
                page.Page += offset;
                layout.Pages.Add(page);
            }

            offset += reading.Pages;
        }

        layout.Total = layout.Pages.Count;
        var blocks = parts.Select(p => PdfReading.ToBlock(p.Fixture)).ToList();
        var block = new Block(id, [.. blocks.SelectMany(b => b.Records)], [.. blocks.SelectMany(b => b.Rows)]);
        var run = new RunResult
        {
            Id = id,
            Configuration = "grid-presplit-report",
            Block = id,
            Footer = true,
            Repetition = 1,
            Purpose = "report",
            Status = "ok",
            Dialogs = 0,
            AbortedRequests = 0,
            NotFoundRequests = 0,
            ScriptsExpected = 0,
            ScriptsFound = 0,
            Layout = layout,
        };
        var checks = AccountingVerifier.VerifyPaged(block, run).Where(c => Account.RuleNumber(c.Name) <= 5).ToList();
        result.Rules = [.. checks.Select(c => new RuleLine(Account.RuleNumber(c.Name), c.Name, c.Outcome, c.FirstFailingPage, c.Detail))];
        result.AccountingFailures = [.. checks.Where(c => !c.Pass).Select(c => $"XA-{Account.RuleNumber(c.Name).ToString(CultureInfo.InvariantCulture)}|p{c.FirstFailingPage?.ToString(CultureInfo.InvariantCulture)}").Distinct()];

        result.GroupFailures = Groups(parts, readings);
        result.Boundaries = Chain(parts, readings);
        return result;
    }

    /// <summary>A printed line of the report, in global reading order.</summary>
    private sealed record PrintedLine(int Part, int Line, int Page, string Kind, string? Id, string Text, int? Seq, string? Amount);

    private static List<PrintedLine> Lines(IReadOnlyList<PartDoc> parts, IReadOnlyList<PdfReading> readings)
    {
        var result = new List<PrintedLine>();
        for (var p = 0; p < parts.Count; p++)
        {
            var reading = readings[p];
            var lines = reading.Document.ContentLines;
            for (var i = 0; i < lines.Count; i++)
            {
                var page = parts[p].Part.FirstGlobalPage + lines[i].Page - 1;
                if (reading.RowOf[i] is { } seq)
                {
                    if (reading.AnchorLine.TryGetValue(seq, out var anchor) && anchor == i)
                    {
                        result.Add(new PrintedLine(p, i, page, "item", null, lines[i].Text, seq, null));
                    }

                    continue;
                }

                var words = lines[i].Words;
                if (words.Count == 0)
                {
                    continue;
                }

                if (reading.Kinds[i] is LineKind.Group && GroupId().IsMatch(words[0].Text))
                {
                    result.Add(new PrintedLine(p, i, page, words[^1].Text == Cont ? "continuation" : "header", words[0].Text, lines[i].Text, null, null));
                }
                else if (reading.Kinds[i] is LineKind.Group && words.Count >= 2 && words[0].Text == "Group" && words[1].Text == "total")
                {
                    result.Add(new PrintedLine(p, i, page, "total", null, lines[i].Text, null, words[^1].Text));
                }
                else if (words.Count >= 2 && words[0].Text == "Block" && words[1].Text == "total")
                {
                    result.Add(new PrintedLine(p, i, page, "block", null, lines[i].Text, null, words[^1].Text));
                }
            }
        }

        return result;
    }

    /// <summary>The source side: every group of the report, its caption, its items' sequences, its sum, and whether it is closed (a group-end row in some part).</summary>
    private sealed record SourceSide(Dictionary<string, (string Caption, List<int> Items, decimal Sum, bool Closed)> Groups, List<string> Order, Dictionary<int, string> GroupOfRow);

    private static SourceSide Source(IReadOnlyList<PartDoc> parts)
    {
        var groups = new Dictionary<string, (string Caption, List<int> Items, decimal Sum, bool Closed)>(StringComparer.Ordinal);
        var order = new List<string>();
        var groupOfRow = new Dictionary<int, string>();
        foreach (var part in parts)
        {
            string? current = null;
            foreach (var row in part.Fixture.Rows)
            {
                switch (row.Kind)
                {
                    case RowKind.GroupStart:
                        current = row.GroupId!;
                        if (!groups.ContainsKey(current))
                        {
                            var name = row.GroupName!.EndsWith(PartFixtures.ContinuationSuffix, StringComparison.Ordinal) ? row.GroupName[..^PartFixtures.ContinuationSuffix.Length] : row.GroupName;
                            groups[current] = ($"{current} — {name}", [], 0m, false);
                            order.Add(current);
                        }

                        break;
                    case RowKind.Item:
                        var g = groups[current!];
                        g.Items.Add(row.Sequence!.Value);
                        groups[current!] = g with { Sum = g.Sum + Parse(row.Amount!) };
                        groupOfRow[row.Sequence!.Value] = current!;
                        break;
                    case RowKind.GroupEnd:
                        groups[current!] = groups[current!] with { Closed = true };
                        break;
                }
            }
        }

        return new SourceSide(groups, order, groupOfRow);
    }

    private static List<GroupFailure> Groups(IReadOnlyList<PartDoc> parts, IReadOnlyList<PdfReading> readings)
    {
        var failures = new List<GroupFailure>();
        var source = Source(parts);
        var lines = Lines(parts, readings);

        // RG-1 headers.
        var plain = lines.Where(l => l.Kind == "header").ToList();
        foreach (var h in lines.Where(l => l.Kind is "header" or "continuation" && !source.Groups.ContainsKey(l.Id!)))
        {
            failures.Add(new GroupFailure("RG-1", "header-unknown", h.Id, null, h.Page, $"'{h.Text}'"));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Kind is not ("header" or "continuation") || !source.Groups.TryGetValue(l.Id!, out var g))
            {
                continue;
            }

            if (l.Kind == "continuation")
            {
                var next = lines.Skip(i + 1).FirstOrDefault(x => x.Kind == "item");
                var nextGroup = next?.Seq is { } s ? source.GroupOfRow.GetValueOrDefault(s) : null;
                if (nextGroup != l.Id)
                {
                    failures.Add(new GroupFailure("RG-1", "continuation-wrong", l.Id, null, l.Page, $"'{l.Text}' is followed by a row of {nextGroup ?? "no group"}"));
                }
                else if (TextNormalisation.N(l.Text) != TextNormalisation.N(g.Caption + " " + Cont))
                {
                    failures.Add(new GroupFailure("RG-1", "header-text", l.Id, null, l.Page, $"printed '{l.Text}', expected '{g.Caption} {Cont}'"));
                }
            }
            else if (TextNormalisation.N(l.Text) != TextNormalisation.N(g.Caption))
            {
                failures.Add(new GroupFailure("RG-1", "header-text", l.Id, null, l.Page, $"printed '{l.Text}', expected '{g.Caption}'"));
            }
        }

        foreach (var id in source.Order)
        {
            var mine = plain.Where(h => h.Id == id).ToList();
            if (mine.Count == 0)
            {
                failures.Add(new GroupFailure("RG-1", "header-missing", id, null, null, $"no plain header line for {source.Groups[id].Caption}"));
            }
            else if (mine.Count > 1)
            {
                failures.Add(new GroupFailure("RG-1", "header-duplicated", id, null, mine[1].Page, $"{mine.Count} plain header lines"));
            }
        }

        var printedOrder = plain.Where(h => source.Groups.ContainsKey(h.Id!)).Select(h => h.Id!).Distinct(StringComparer.Ordinal).ToList();
        var sourceOrder = source.Order.Where(printedOrder.Contains).ToList();
        if (!printedOrder.SequenceEqual(sourceOrder, StringComparer.Ordinal))
        {
            failures.Add(new GroupFailure("RG-1", "header-order", null, null, null, $"printed {string.Join(",", printedOrder)}, source {string.Join(",", sourceOrder)}"));
        }

        // RG-2 group totals: a total line belongs to the group of the row printed just before it.
        var totals = new List<(int Index, string? Group, PrintedLine Line)>();
        string? lastGroup = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == "item")
            {
                lastGroup = source.GroupOfRow.GetValueOrDefault(lines[i].Seq!.Value);
            }
            else if (lines[i].Kind == "total")
            {
                totals.Add((i, lastGroup, lines[i]));
            }
        }

        foreach (var t in totals.Where(t => t.Group is null))
        {
            failures.Add(new GroupFailure("RG-2", "total-unexpected", null, null, t.Line.Page, "a total line before any row"));
        }

        foreach (var id in source.Order)
        {
            var g = source.Groups[id];
            var mine = totals.Where(t => t.Group == id).ToList();
            if (!g.Closed)
            {
                if (mine.Count > 0)
                {
                    failures.Add(new GroupFailure("RG-2", "total-unexpected", id, null, mine[0].Line.Page, $"the group is open but a total '{mine[0].Line.Amount}' prints"));
                }

                continue;
            }

            if (mine.Count == 0)
            {
                failures.Add(new GroupFailure("RG-2", "total-missing", id, null, null, $"expected {Format(g.Sum)}"));
                continue;
            }

            if (mine.Count > 1)
            {
                failures.Add(new GroupFailure("RG-2", "total-duplicated", id, null, mine[1].Line.Page, $"{mine.Count} total lines"));
            }

            if (GroupCheck.TryValue(mine[0].Line.Amount) != g.Sum)
            {
                failures.Add(new GroupFailure("RG-2", "total-wrong", id, null, mine[0].Line.Page, $"printed {mine[0].Line.Amount}, expected {Format(g.Sum)}"));
            }
        }

        // RG-3 membership, for groups whose plain header printed.
        var itemIndex = new Dictionary<int, int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == "item")
            {
                itemIndex[lines[i].Seq!.Value] = i;
            }
        }

        foreach (var id in source.Order)
        {
            var header = lines.FindIndex(l => l.Kind == "header" && l.Id == id);
            if (header < 0)
            {
                continue;
            }

            var g = source.Groups[id];
            var total = g.Closed ? totals.FirstOrDefault(t => t.Group == id) : default;
            var end = total.Line is null ? int.MaxValue : total.Index;
            foreach (var seq in g.Items)
            {
                if (itemIndex.TryGetValue(seq, out var at) && (at < header || at > end))
                {
                    failures.Add(new GroupFailure("RG-3", "row-outside-group", id, seq, lines[at].Page, $"row {seq} outside {id}"));
                }
            }

            foreach (var (seq, at) in itemIndex)
            {
                if (at > header && at < end && source.GroupOfRow.TryGetValue(seq, out var other) && other != id)
                {
                    failures.Add(new GroupFailure("RG-3", "row-outside-group", other, seq, lines[at].Page, $"row {seq} of {other} inside {id}"));
                }
            }
        }

        // RG-4 part totals.
        for (var p = 0; p < parts.Count; p++)
        {
            var expected = parts[p].Fixture.Items.Sum(i => Parse(i.Amount!));
            var printed = lines.Where(l => l.Part == p && l.Kind == "block").ToList();
            if (printed.Count != 1 || GroupCheck.TryValue(printed[0].Amount) != expected)
            {
                failures.Add(new GroupFailure("RG-4", "part-total-wrong", $"part {(p + 1).ToString(CultureInfo.InvariantCulture)}", null, printed.FirstOrDefault()?.Page,
                    $"printed {string.Join(",", printed.Select(x => x.Amount))}, expected {Format(expected)}"));
            }
        }

        return failures;
    }

    /// <summary>PB-7: for each boundary, the next part's first "Brought forward" against the previous part's last "Carried forward".</summary>
    private static List<BoundaryChain> Chain(IReadOnlyList<PartDoc> parts, IReadOnlyList<PdfReading> readings)
    {
        var result = new List<BoundaryChain>();
        for (var k = 1; k < parts.Count; k++)
        {
            var carried = FooterValue(readings[k - 1].FooterThirds(readings[k - 1].Pages).Right);
            var brought = FooterValue(readings[k].FooterThirds(1).Left);
            var state = carried is not null && brought is not null && GroupCheck.TryValue(carried) == GroupCheck.TryValue(brought) ? "continuous" : "broken";
            result.Add(new BoundaryChain(k + 1, parts[k].Part.FirstGlobalPage, carried, brought, state));
        }

        return result;
    }

    /// <summary>The last "Carried forward" amount a part prints.</summary>
    public static string FooterRight(PartDoc part)
    {
        var reading = part.Read();
        return FooterValue(reading.FooterThirds(reading.Pages).Right) ?? throw new InvalidOperationException($"{part.Part.Fixture}: no carried forward on its last page");
    }

    private static string? FooterValue(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var last = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last is not null && GroupCheck.TryValue(last) is not null ? last : null;
    }

    /// <summary>PB-1 … PB-7 for one instance (observations, never pass or fail).</summary>
    public static JsonObject Observe(ReportInstance instance, IReadOnlyList<PartDoc> parts, JsonArray boundaries, ReportResult result)
    {
        var readings = parts.Select(p => p.Read()).ToList();
        var lines = Lines(parts, readings);
        var source = Source(parts);
        var lastPages = parts.Select(p => p.Part.FirstGlobalPage + p.Part.Pages - 1).ToList();
        var firstPages = parts.Select(p => p.Part.FirstGlobalPage).ToList();

        // Item pages per group (global), from each row's pages.
        var itemPages = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        for (var p = 0; p < parts.Count; p++)
        {
            foreach (var (seq, pages) in readings[p].RowPages)
            {
                if (source.GroupOfRow.TryGetValue(seq, out var g))
                {
                    if (!itemPages.TryGetValue(g, out var set))
                    {
                        set = [];
                        itemPages[g] = set;
                    }

                    foreach (var page in pages)
                    {
                        set.Add(parts[p].Part.FirstGlobalPage + page - 1);
                    }
                }
            }
        }

        // PB-1 header left alone at a page end (a header line with no row of its group on its page).
        var alone = lines.Where(l => l.Kind is "header" or "continuation" && !(itemPages.GetValueOrDefault(l.Id!)?.Contains(l.Page) ?? false))
            .Select(l => new JsonObject { ["group"] = l.Id, ["page"] = l.Page, ["form"] = l.Kind, ["partLastPage"] = lastPages.Contains(l.Page) }).ToList();

        // Behaviour 1 at each boundary: the part's last page does not end with a lone header.
        var b1 = new JsonArray();
        for (var k = 0; k < parts.Count - 1; k++)
        {
            var last = lines.LastOrDefault(l => l.Part == k && l.Kind is "header" or "continuation" or "item" or "total" or "block");
            var lone = last is not null && last.Kind is "header" or "continuation" && !(itemPages.GetValueOrDefault(last.Id!)?.Contains(last.Page) ?? false);
            b1.Add(new JsonObject { ["beforePart"] = k + 2, ["lastLine"] = last?.Kind, ["produced"] = !lone });
        }

        // PB-2 header repetition: at each boundary with an open group, whether the next part's first detail line is its continuation header;
        // pages of multi-page groups without a header line of the group, inside parts and at part starts.
        var b2 = new JsonArray();
        foreach (var bnode in boundaries)
        {
            var before = bnode!["beforePart"]!.GetValue<int>();
            var open = bnode["openGroup"]?.GetValue<string>();
            if (open is null)
            {
                continue;
            }

            var firstDetail = lines.FirstOrDefault(l => l.Part == before - 1 && l.Kind is "header" or "continuation" or "item" or "total");
            b2.Add(new JsonObject { ["beforePart"] = before, ["group"] = open, ["firstLine"] = firstDetail?.Kind, ["produced"] = firstDetail is { Kind: "continuation" } && firstDetail.Id == open });
        }

        var headerPages = lines.Where(l => l.Kind is "header" or "continuation").GroupBy(l => l.Id!).ToDictionary(g => g.Key, g => g.Select(l => l.Page).ToHashSet());
        var withoutHeaderInPart = 0;
        var withoutHeaderAtStart = 0;
        var inPartHeaderRepeats = 0;
        foreach (var (g, pages) in itemPages.Where(kv => kv.Value.Count > 1))
        {
            foreach (var page in pages.Skip(1))
            {
                var has = headerPages.GetValueOrDefault(g)?.Contains(page) ?? false;
                if (firstPages.Contains(page))
                {
                    withoutHeaderAtStart += has ? 0 : 1;
                }
                else if (has)
                {
                    inPartHeaderRepeats++;
                }
                else
                {
                    withoutHeaderInPart++;
                }
            }
        }

        // PB-3 total placement; behaviour 3 at the boundary (groups closing in a part's last three records, or whose last rows open the next part).
        var totals = new List<(PrintedLine Line, string? Group)>();
        string? lastGroup = null;
        foreach (var l in lines)
        {
            if (l.Kind == "item")
            {
                lastGroup = source.GroupOfRow.GetValueOrDefault(l.Seq!.Value);
            }
            else if (l.Kind == "total")
            {
                totals.Add((l, lastGroup));
            }
        }

        var placements = new JsonArray();
        var b3 = new JsonArray();
        var separated = 0;
        foreach (var id in source.Order)
        {
            var g = source.Groups[id];
            var t = totals.FirstOrDefault(x => x.Group == id);
            if (t.Line is null)
            {
                continue;
            }

            var lastSeq = g.Items[^1];
            var lastLine = lines.LastOrDefault(l => l.Kind == "item" && l.Seq == lastSeq);
            var lastRowPage = itemPages.GetValueOrDefault(id)?.Max;
            var same = lastRowPage == t.Line.Page;
            separated += same ? 0 : 1;
            var partOfLast = lastLine?.Part;
            var partItems = partOfLast is { } pi ? parts[pi].Fixture.Items.Select(x => x.Sequence!.Value).ToList() : [];
            var closesNearEnd = partItems.Count > 0 && partItems.IndexOf(lastSeq) >= partItems.Count - 3 && partOfLast < parts.Count - 1;
            var opensNext = partOfLast is > 0 && boundaries.Any(b => b!["beforePart"]!.GetValue<int>() == partOfLast + 1 && b["openGroup"]?.GetValue<string>() == id && b["class"]?.GetValue<string>() == "near-end");
            placements.Add(new JsonObject { ["group"] = id, ["lastRowPage"] = lastRowPage, ["totalPage"] = t.Line.Page, ["samePage"] = same });
            if (closesNearEnd || opensNext)
            {
                b3.Add(new JsonObject { ["group"] = id, ["case"] = closesNearEnd ? "closes in a part's last rows" : "last rows open the next part", ["produced"] = same, ["totalPage"] = t.Line.Page, ["lastRowPage"] = lastRowPage });
            }
        }

        // PB-4 the last page of each part.
        var lastPageNodes = new JsonArray();
        for (var p = 0; p < parts.Count; p++)
        {
            var doc = parts[p].Document;
            var pageLines = doc.ContentLines.Where(l => l.Page == doc.Pages).ToList();
            var bottom = pageLines.SelectMany(l => l.Words).Select(w => w.YMax).DefaultIfEmpty(PrintedDocument.TopPt).Max();
            var kinds = lines.Where(l => l.Part == p && l.Page == lastPages[p]).Select(l => l.Kind).ToList();
            lastPageNodes.Add(new JsonObject
            {
                ["part"] = p + 1,
                ["page"] = lastPages[p],
                ["contentLines"] = pageLines.Count,
                ["lastContentBottomPt"] = Math.Round(bottom, 1),
                ["contentFill"] = Math.Round((bottom - PrintedDocument.TopPt) / (PrintedDocument.BottomPt - PrintedDocument.TopPt), 3),
                ["onlyGroupOrTotalLines"] = kinds.Count > 0 && kinds.All(k => k is "header" or "continuation" or "total" or "block"),
            });
        }

        // PB-5 pages without detail.
        var withoutDetail = new JsonArray();
        for (var p = 0; p < parts.Count; p++)
        {
            for (var page = 1; page <= readings[p].Pages; page++)
            {
                if (page <= readings[p].WithoutDetail.Length && readings[p].WithoutDetail[page - 1])
                {
                    withoutDetail.Add(parts[p].Part.FirstGlobalPage + page - 1);
                }
            }
        }

        return new JsonObject
        {
            ["instance"] = instance.Instance,
            ["report"] = instance.Report,
            ["variant"] = instance.Variant,
            ["pages"] = result.Pages,
            ["pb1HeadersAlone"] = new JsonArray([.. alone]),
            ["pb1HeadersAloneCount"] = alone.Count,
            ["pb1AtPartLastPage"] = alone.Count(a => a["partLastPage"]!.GetValue<bool>()),
            ["behaviour1AtBoundary"] = b1,
            ["behaviour2AtBoundary"] = b2,
            ["pb2PagesWithoutHeaderInParts"] = withoutHeaderInPart,
            ["pb2PartStartsWithoutHeader"] = withoutHeaderAtStart,
            ["behaviour2OnInPartContinuationPages"] = inPartHeaderRepeats,
            ["pb3Totals"] = placements,
            ["pb3TotalsSeparated"] = separated,
            ["behaviour3AtBoundary"] = b3,
            ["pb4PartLastPages"] = lastPageNodes,
            ["pb5PagesWithoutDetail"] = withoutDetail,
            ["pb6Boundaries"] = boundaries.DeepClone(),
            ["pb7Chain"] = new JsonArray([.. result.Boundaries.Select(b => (JsonNode)new JsonObject
            {
                ["beforePart"] = b.BeforePart,
                ["page"] = b.Page,
                ["carriedBefore"] = b.CarriedBefore,
                ["broughtAfter"] = b.BroughtAfter,
                ["state"] = b.State,
            })]),
        };
    }

    private static decimal Parse(string text) => decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^G\d{4}$")]
    private static partial Regex GroupId();
}
