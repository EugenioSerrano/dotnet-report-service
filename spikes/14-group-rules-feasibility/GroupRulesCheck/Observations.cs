using GroupContinuityCheck;
using PageAccountingCheck;
using PaginationFixtures;

namespace GroupRulesCheck;

/// <summary>One behaviour's observation in one run: its instances, which were produced, which were not, and (repeated header) the spurious lines.</summary>
internal sealed class BehaviourCount
{
    public int Instances { get; set; }

    public int Produced { get; set; }

    /// <summary>Instances produced, as "&lt;group&gt;" (header, total) or "&lt;group&gt; p&lt;page&gt;" (repeated header).</summary>
    public List<string> ProducedInstances { get; set; } = [];

    /// <summary>Instances not produced, with their pages and, for the repeated header, the kind (missing, more than one, after a line of the group).</summary>
    public List<string> Violations { get; set; } = [];

    /// <summary>The groups (header, total) or group-page pairs (repeated header) that are violations.</summary>
    public List<string> ViolatedInstances { get; set; } = [];

    /// <summary>Repeated-header lines on a page where their group does not continue.</summary>
    public List<string> Spurious { get; set; } = [];
}

/// <summary>The three behaviour observations of one run.</summary>
internal sealed class BehaviourResult
{
    public BehaviourCount KeepHeader { get; set; } = new();

    public BehaviourCount RepeatHeader { get; set; } = new();

    public BehaviourCount KeepTotal { get; set; } = new();

    public BehaviourCount Of(string behaviour) => behaviour switch
    {
        Configurations.KeepHeader => KeepHeader,
        Configurations.RepeatHeader => RepeatHeader,
        Configurations.KeepTotal => KeepTotal,
        _ => throw new ArgumentException($"Unknown behaviour '{behaviour}'.", nameof(behaviour)),
    };
}

/// <summary>
/// The behaviour observation rules, written before any run (README, "Observation
/// rules and classes"). They read only the reading (through the unchanged group
/// check's placement), the lines the reading rule set aside, and the source fixture.
/// <list type="bullet">
/// <item>A header never left alone at a page end: per group whose own header is read, produced when the header's page holds a line of one of the group's rows (the group check's P-1 does not list it).</item>
/// <item>The header repeated with "(cont.)": per group and later page holding a line of the group's rows or its total, produced when that page holds exactly one repeated-header line of the group, before every line of the group; a repeated-header line on any other page is spurious.</item>
/// <item>The group total kept with its last row: per closed group whose total is read, produced when the total is on the page of the last line of the group's last row (P-3).</item>
/// </list>
/// </summary>
internal static class BehaviourObservations
{
    public static BehaviourResult Observe(ReadingResult reading, PdfReading pdf, GroupResult groups, Fixture fixture)
    {
        var result = new BehaviourResult();
        foreach (var g in groups.Groups.Where(g => g.HeaderLines > 0))
        {
            var b1 = result.KeepHeader;
            b1.Instances++;
            if (g.HeaderAlonePages.Count == 0)
            {
                b1.Produced++;
                b1.ProducedInstances.Add(g.Group);
            }
            else
            {
                b1.ViolatedInstances.Add(g.Group);
                b1.Violations.Add($"{g.Group} alone p{string.Join(",p", g.HeaderAlonePages)}");
            }
        }

        foreach (var g in groups.Groups.Where(g => g.TotalPage is not null && g.LastItemPage is not null))
        {
            var b3 = result.KeepTotal;
            b3.Instances++;
            if (g.TotalPage == g.LastItemPage)
            {
                b3.Produced++;
                b3.ProducedInstances.Add(g.Group);
            }
            else
            {
                b3.ViolatedInstances.Add(g.Group);
                b3.Violations.Add($"{g.Group} total p{g.TotalPage}, last row p{g.LastItemPage}");
            }
        }

        RepeatHeader(result.RepeatHeader, reading, pdf, groups, fixture);
        return result;
    }

    private static void RepeatHeader(BehaviourCount b2, ReadingResult reading, PdfReading pdf, GroupResult groups, Fixture fixture)
    {
        // The group of every line of the reading that belongs to a group: its rows' lines, and its total line
        // (a total belongs to the group of the row printed just before it, as the group check reads it).
        var groupOfRow = new Dictionary<int, string>();
        foreach (var g in SourceGroups.Of(fixture))
        {
            foreach (var item in g.Items)
            {
                groupOfRow[item.Sequence!.Value] = g.Id;
            }
        }

        var lines = pdf.Document.ContentLines;
        var firstLineOf = new Dictionary<(string Group, int Page), int>();
        string? lastItemGroup = null;
        for (var i = 0; i < lines.Count; i++)
        {
            string? group = null;
            if (pdf.RowOf[i] is { } seq)
            {
                lastItemGroup = groupOfRow.GetValueOrDefault(seq);
                group = lastItemGroup;
            }
            else if (lines[i].Words.Count >= 2 && lines[i].Words[0].Text == "Group" && lines[i].Words[1].Text == "total")
            {
                group = lastItemGroup;
            }

            if (group is not null && !firstLineOf.ContainsKey((group, lines[i].Page)))
            {
                firstLineOf[(group, lines[i].Page)] = reading.KeptIndex[i];
            }
        }

        var instancePages = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var g in groups.Groups.Where(g => g.HeaderPages.Count > 0))
        {
            var headerPage = g.HeaderPages.Min();
            var pages = new SortedSet<int>(g.ItemPages.Where(p => p > headerPage));
            if (g.TotalPage is { } tp && tp > headerPage)
            {
                pages.Add(tp);
            }

            instancePages[g.Group] = pages;
            foreach (var q in pages)
            {
                b2.Instances++;
                var mine = reading.Removed.Where(r => r.Group == g.Group && r.Page == q).ToList();
                var first = firstLineOf.TryGetValue((g.Group, q), out var at) ? at : int.MaxValue;
                string? kind = mine.Count switch
                {
                    0 => "missing",
                    > 1 => $"{mine.Count} lines",
                    _ when mine[0].Index > first => "after a line of the group",
                    _ => null,
                };
                if (kind is null)
                {
                    b2.Produced++;
                    b2.ProducedInstances.Add($"{g.Group} p{q}");
                }
                else
                {
                    b2.ViolatedInstances.Add($"{g.Group} p{q}");
                    b2.Violations.Add($"{g.Group} p{q}: {kind}");
                }
            }
        }

        foreach (var r in reading.Removed)
        {
            if (!instancePages.TryGetValue(r.Group, out var pages) || !pages.Contains(r.Page))
            {
                b2.Spurious.Add($"{r.Group} p{r.Page}");
            }
        }
    }
}
