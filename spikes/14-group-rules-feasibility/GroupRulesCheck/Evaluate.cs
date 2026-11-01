using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using GroupContinuityCheck;
using PageAccountingCheck;
using PaginationFixtures;
using PdfLayoutSpike;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace GroupRulesCheck;

/// <summary>Everything the checks say about one planned run (evaluation.json).</summary>
internal sealed class EvaluatedEntry
{
    public string Run { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Configuration { get; set; } = Configurations.None;

    public string? Mutation { get; set; }

    public bool FooterOn { get; set; }

    public int Repetition { get; set; }

    public int MatrixRow { get; set; }

    public string? Anchor { get; set; }

    /// <summary>checked | no-pdf | no-check.</summary>
    public string Verdict { get; set; } = "no-pdf";

    public string? Reason { get; set; }

    /// <summary>The repeated-header lines the reading rule set aside.</summary>
    public List<RemovedLine> Removed { get; set; } = [];

    /// <summary>The text verdict of record: the unchanged text rules on the reading.</summary>
    public IntegrityEntry? Text { get; set; }

    /// <summary>The unchanged strict accounting on the reading (rules 1-9, the full evaluation, characters), with rule 7 against the twin's reading.</summary>
    public StrictEntry Accounting { get; set; } = new();

    /// <summary>The unchanged group check on the reading (GC-1 … GC-4, P-1 … P-5).</summary>
    public GroupResult? Groups { get; set; }

    public BehaviourResult? Behaviours { get; set; }

    public double CheckSeconds { get; set; }
}

/// <summary>
/// The "evaluate" command. For every planned run with a PDF: one extraction; the
/// reading rule; the text rules of spikes/07 on the reading (the text verdict of
/// record, its entry built as that folder's check command builds it); the strict
/// accounting of spikes/10-pagedjs-table-strict-accounting on the reading, with rule 7
/// against the footer-off twin of the same fixture, configuration and repetition read
/// the same way (the twin step of that folder's evaluation, copied); the group check
/// of spikes/12-group-continuity on the reading; the three behaviour observations.
/// </summary>
internal static class EvaluateCommand
{
    private const int MaxFailuresKept = 100;

    public static async Task<int> RunAsync(string fixturesDirectory, string runsDirectory, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var started = DateTimeOffset.Now;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var stage = StageFolder.Load(runsDirectory);
        var plan = RulesPlan.Load(runsDirectory);
        var fixtures = new Dictionary<string, Fixture>(StringComparer.Ordinal);
        var entries = new List<EvaluatedEntry>();
        foreach (var planned in plan.Runs)
        {
            if (!fixtures.TryGetValue(planned.FixtureId, out var fixture))
            {
                fixture = FixtureFile.ReadVerified(fixturesDirectory, planned.FixtureId, sums);
                fixtures[planned.FixtureId] = fixture;
            }

            var entry = await EvaluateAsync(planned, plan, stage, fixture, timeout, cancellation);
            entries.Add(entry);
            var b = entry.Behaviours;
            Console.WriteLine($"{entry.Run}: {entry.Accounting.RunClass} {entry.Verdict}, pages {entry.Accounting.Pages}, removed {entry.Removed.Count}, text {entry.Text?.Verdict}, "
                + $"accounting {entry.Accounting.AccountingVerdict}, groups {entry.Groups?.Failures.Count.ToString(CultureInfo.InvariantCulture) ?? "-"}"
                + (b is null ? string.Empty : $", header {b.KeepHeader.Produced}/{b.KeepHeader.Instances}, repeated {b.RepeatHeader.Produced}/{b.RepeatHeader.Instances} (spurious {b.RepeatHeader.Spurious.Count}), total {b.KeepTotal.Produced}/{b.KeepTotal.Instances}"));
        }

        var checker = new JsonObject
        {
            ["poppler"] = await Account.PopplerVersionAsync(cancellation),
            ["startedAt"] = started.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["endedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["timeoutSeconds"] = timeoutSeconds,
            ["fixtureSumsSha256"] = Sums.HashFile(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName)),
        };
        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "evaluation.json"), new { checker, runs = entries });
        Console.WriteLine($"{entries.Count} entries: {entries.Count(e => e.Verdict == "checked")} checked, {entries.Count(e => e.Verdict == "no-pdf")} no-pdf, {entries.Count(e => e.Verdict == "no-check")} no-check.");
        return 0;
    }

    public static List<EvaluatedEntry> Read(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<List<EvaluatedEntry>>(JsonNode.Parse(File.ReadAllText(path))!["runs"]!.ToJsonString(), StageFolder.Json) ?? [];

    /// <summary>One extraction, the reading and every unchanged check on it.</summary>
    public static (ReadingResult Reading, PdfReading Pdf, IntegrityResult Text) Read(PrintedDocument printed, Fixture fixture, bool footerOn)
    {
        var marks = footerOn ? IntegrityRules.MarksExpected : IntegrityRules.MarksNone;
        var footer = footerOn ? IntegrityRules.FooterAccounting : IntegrityRules.FooterNone;
        var reading = RepeatedHeaders.Apply(printed, fixture);
        var text = new IntegrityRules(fixture, marks, footer).Analyze(reading.Reading);
        var pdf = PdfReading.From(reading.Reading, fixture, marks, footer);
        return (reading, pdf, text);
    }

    /// <summary>The text entry of a result, field by field as the check command of spikes/07 writes it.</summary>
    public static IntegrityEntry TextEntry(string runId, string? mutation, IntegrityResult analysis) => new()
    {
        RunId = runId,
        Mutation = mutation,
        Verdict = analysis.Pass ? "pass" : "fail",
        Pages = analysis.Pages,
        ContentSha256 = analysis.ContentSha256,
        PageWordCounts = analysis.PageWordCounts,
        FailureCount = analysis.Failures.Count,
        Failures = [.. analysis.Failures.Take(MaxFailuresKept).Select(f => new IntegrityFailure
        {
            Rule = f.Rule,
            Class = f.Class,
            Row = f.Row,
            Region = f.Region,
            Field = f.Field,
            Page = f.Page,
            Detail = f.Detail,
        })],
        Rules = analysis.Rules,
    };

    private static async Task<EvaluatedEntry> EvaluateAsync(RulesPlanned planned, RulesPlan plan, StageFolder stage, Fixture fixture, TimeSpan timeout, CancellationToken cancellation)
    {
        var run = stage.Runs.GetValueOrDefault(planned.RunId);
        var entry = new EvaluatedEntry
        {
            Run = planned.RunId,
            FixtureId = planned.FixtureId,
            Configuration = planned.Configuration,
            Mutation = planned.Mutation,
            FooterOn = planned.FooterOn,
            Repetition = planned.Repetition,
            MatrixRow = planned.MatrixRow,
            Anchor = planned.Anchor,
        };
        var a = entry.Accounting;
        a.Run = planned.RunId;
        a.Stage = planned.Stage;
        a.Markup = planned.Markup;
        a.FixtureId = planned.FixtureId;
        a.Mutation = planned.Mutation;
        a.Repetition = planned.Repetition;
        a.MatrixRow = planned.MatrixRow;
        a.Split = planned.Split;
        a.Trigger = planned.Trigger;
        a.PartLimit = planned.PartLimit;
        a.FooterOn = planned.FooterOn;
        a.RunClass = run?.Outcome ?? "missing";
        a.RunReason = run?.Reason;
        a.AccountingVerdict = "n/a";
        if (run is not null)
        {
            a.LayoutRepeated = run.LayoutRepeated;
            a.SemPass = run.Outcome == "ok" ? run.SemPass : null;
            a.SemInnerSha256 = run.SemInnerSha256;
            a.HtmlBytes = run.HtmlBytes;
            a.LibraryRules = run.OwnRules;
            a.Measurements = new JsonObject
            {
                ["wallMs"] = run.WallMs,
                ["measureMs"] = run.MeasureMs,
                ["splitMs"] = run.SplitMs,
                ["layoutMs"] = run.LayoutMs,
                ["pagedFlowMs"] = run.PagedFlowMs,
                ["pdfMs"] = run.PdfMs,
                ["totalMs"] = run.TotalMs,
                ["chromiumTreePeakRssKiB"] = run.ChromiumTreePeakRssKiB,
                ["chromiumTreePeakPssKiB"] = run.ChromiumTreePeakPssKiB,
                ["harnessVmHwmKiB"] = run.HarnessVmHwmKiB,
                ["cgroupMemoryPeakBytes"] = run.CgroupMemoryPeakBytes,
                ["splitRows"] = run.SplitRows,
                ["parts"] = run.Parts,
                ["pdfBytes"] = run.PdfBytes,
                ["libraryPages"] = run.Pages,
                ["consoleMessages"] = run.ConsoleTotal,
                ["layoutRepeatedError"] = run.LayoutRepeatedError,
                ["layoutRepeatedAnyLevel"] = run.LayoutRepeatedAnyLevel,
            };
        }

        var pdfPath = run?.Pdf is null ? null : Path.Combine(stage.Directory, run.Pdf);
        if (run is null || run.Outcome != "ok" || pdfPath is null || !File.Exists(pdfPath))
        {
            entry.Verdict = a.Verdict = "no-pdf";
            entry.Reason = a.Reason = run is null ? "no run record" : run.Outcome + (run.Reason is null ? string.Empty : $" ({run.Reason})");
            return entry;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var printed = PrintedDocument.From(await Poppler.ExtractAsync(pdfPath, timeout, cancellation));
            var (reading, pdf, analysis) = Read(printed, fixture, planned.FooterOn);
            entry.Removed = reading.Removed;
            entry.Text = TextEntry(planned.RunId, planned.Mutation, analysis);
            a.TextVerdict = entry.Text.Verdict;
            a.TextFailureCount = entry.Text.FailureCount;
            a.ContentSha256 = entry.Text.ContentSha256;
            Account.Judge(a, pdf, fixture, run, entry.Text);

            // Rule 7: this footer-on run against its footer-off twin (same fixture, configuration, split, values and repetition), both read the same way.
            if (planned.FooterOn)
            {
                var twin = plan.Runs.FirstOrDefault(p => !p.FooterOn && p.FixtureId == planned.FixtureId && p.Mutation == planned.Mutation && p.Configuration == planned.Configuration
                    && p.Split == planned.Split && p.Trigger == planned.Trigger && p.PartLimit == planned.PartLimit && p.Repetition == planned.Repetition);
                if (twin is not null && stage.Runs.GetValueOrDefault(twin.RunId) is { Outcome: "ok", Pdf: not null } twinRun)
                {
                    var twinPrinted = PrintedDocument.From(await Poppler.ExtractAsync(Path.Combine(stage.Directory, twinRun.Pdf), timeout, cancellation));
                    var (_, off, _) = Read(twinPrinted, fixture, false);
                    var check = AccountingVerifier.CompareLayouts(AccountingVerifier.FooterNoDisplacement, off.ToLayout(), pdf.ToLayout());
                    a.Rule7 = new RuleLine(7, check.Name, check.Outcome, check.FirstFailingPage, check.Detail);
                    a.Rule7Pair = twin.RunId;
                    if (!check.Pass)
                    {
                        a.FailureSet.Add($"{check.Name}|p{check.FirstFailingPage}");
                    }
                }
                else if (twin is not null)
                {
                    a.Rule7Pair = twin.RunId;
                    a.Rule7 = new RuleLine(7, AccountingVerifier.FooterNoDisplacement, "NOT EVALUATED", null, "the footer-off twin has no PDF");
                }
            }

            entry.Groups = GroupCheck.Check(pdf, fixture);
            entry.Behaviours = BehaviourObservations.Observe(reading, pdf, entry.Groups, fixture);
            entry.Verdict = a.Verdict = "checked";
        }
        catch (Exception e) when (e is TimeoutException or InvalidDataException or IOException or System.Xml.XmlException or System.ComponentModel.Win32Exception or FormatException or InvalidOperationException or OutOfMemoryException)
        {
            entry.Verdict = a.Verdict = "no-check";
            entry.Reason = a.Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
        }

        entry.CheckSeconds = a.CheckSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
        return entry;
    }
}
