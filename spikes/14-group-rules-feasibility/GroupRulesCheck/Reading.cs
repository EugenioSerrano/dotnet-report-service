using System.Text.Json;
using GroupContinuityCheck;
using PaginationFixtures;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace GroupRulesCheck;

/// <summary>A content line the reading rule set aside: its page, its group, its position among the page's lines and in the printed document.</summary>
internal sealed record RemovedLine(int Page, string Group, int IndexOnPage, int Index);

/// <summary>The printed document with the repeated-header lines set aside, and where every kept line was.</summary>
internal sealed record ReadingResult(PrintedDocument Reading, List<RemovedLine> Removed, List<int> KeptIndex);

/// <summary>
/// The reading rule for repeated headers. A content line is a repeated-header line
/// only when its normalised text equals the normalised "&lt;group id&gt; — &lt;group
/// name&gt; (cont.)" of a group of the run's own fixture. Nothing else is ever
/// matched: not a line with any other text, not a group the block does not hold, and
/// not the "(continues)" mark of a split row (a different text, printed in the
/// amount column of the row). The reading is a copy of the printed document without
/// those lines; the checks, linked unchanged, read the reading.
/// </summary>
internal static class RepeatedHeaders
{
    public const string Suffix = " (cont.)";

    public static ReadingResult Apply(PrintedDocument printed, Fixture fixture)
    {
        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in SourceGroups.Of(fixture))
        {
            forms[TextNormalisation.N(g.Caption + Suffix)] = g.Id;
        }

        var copy = printed.Clone();
        var removed = new List<RemovedLine>();
        var kept = new List<Line>();
        var keptIndex = new List<int>();
        var page = -1;
        var onPage = 0;
        for (var i = 0; i < copy.ContentLines.Count; i++)
        {
            var line = copy.ContentLines[i];
            if (line.Page != page)
            {
                page = line.Page;
                onPage = 0;
            }

            if (forms.TryGetValue(TextNormalisation.N(line.Text), out var id))
            {
                removed.Add(new RemovedLine(line.Page, id, onPage, i));
            }
            else
            {
                kept.Add(line);
                keptIndex.Add(i);
            }

            onPage++;
        }

        copy.ContentLines.Clear();
        copy.ContentLines.AddRange(kept);
        return new ReadingResult(copy, removed, keptIndex);
    }
}

/// <summary>One planned run of this experiment's plan.json (the harness's fields, the configuration and the anchor included).</summary>
internal sealed class RulesPlanned
{
    public string RunId { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Markup { get; set; } = string.Empty;

    public int Repetition { get; set; }

    public string? Mutation { get; set; }

    public string Split { get; set; } = string.Empty;

    public double? Trigger { get; set; }

    public double? PartLimit { get; set; }

    public bool FooterOn { get; set; }

    public int Stage { get; set; }

    public int MatrixRow { get; set; }

    public string Configuration { get; set; } = "none";

    public string? Anchor { get; set; }
}

internal sealed class RulesPlan
{
    public string Matrix { get; set; } = string.Empty;

    public List<RulesPlanned> Runs { get; set; } = [];

    public static RulesPlan Load(string runsDirectory) =>
        JsonSerializer.Deserialize<RulesPlan>(File.ReadAllText(Path.Combine(runsDirectory, "plan.json")), StageFolder.Json)
            ?? throw new InvalidDataException("Empty plan.");
}

/// <summary>The configurations and their rules, as the harness names them.</summary>
internal static class Configurations
{
    public const string None = "none";
    public const string KeepHeader = "keep-header";
    public const string RepeatHeader = "repeat-header";
    public const string KeepTotal = "keep-total";
    public const string AllThree = "all-three";

    public static readonly string[] All = [None, KeepHeader, RepeatHeader, KeepTotal, AllThree];

    public static readonly string[] Behaviours = [KeepHeader, RepeatHeader, KeepTotal];

    public static bool Attempts(string configuration, string behaviour) => configuration == behaviour || configuration == AllThree;
}
