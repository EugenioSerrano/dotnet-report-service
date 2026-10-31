using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using PaginationFixtures;
using PdfLayoutSpike;
using TextIntegrityCheck;

namespace StrictAccountingCheck;

/// <summary>A text-oracle failure as recorded in integrity.json, with whether it may be attributed to column geometry.</summary>
internal sealed record TextFailure(string Rule, string Class, int? Row, string? Field, string? Region, int? Page, string? Detail, bool GeometryCandidate);

/// <summary>The checker's entry for one planned run (accounting.json).</summary>
internal sealed class StrictEntry
{
    public string Run { get; set; } = string.Empty;

    public int Stage { get; set; }

    public string Markup { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string? Mutation { get; set; }

    public int Repetition { get; set; }

    public int MatrixRow { get; set; }

    public string Split { get; set; } = string.Empty;

    public double? Trigger { get; set; }

    public double? PartLimit { get; set; }

    public bool FooterOn { get; set; }

    /// <summary>The run's class as the harness recorded it (ok, timeout, killed-wall-clock, oom-killed, crashed, error, not-run).</summary>
    public string RunClass { get; set; } = string.Empty;

    public string? RunReason { get; set; }

    /// <summary>checked | no-pdf | no-check.</summary>
    public string Verdict { get; set; } = "no-pdf";

    public string? Reason { get; set; }

    public int? Pages { get; set; }

    /// <summary>Rules 1-9 as the unchanged verifier printed them, from this checker's reading of the PDF.</summary>
    public List<RuleLine> Rules { get; set; } = [];

    /// <summary>Rule 7 between this footer-on run and its footer-off twin, when the plan has one.</summary>
    public RuleLine? Rule7 { get; set; }

    public string? Rule7Pair { get; set; }

    /// <summary>The full evaluation of rules 1, 4 and 5 over every page, when one of them fails.</summary>
    public List<FullItem> FullEvaluation { get; set; } = [];

    /// <summary>The accounting rules' failures, each "name|p&lt;page&gt;" (observations excluded).</summary>
    public List<string> FailureSet { get; set; } = [];

    /// <summary>exact | the first failing rule, page and detail | n/a (footer off).</summary>
    public string AccountingVerdict { get; set; } = string.Empty;

    /// <summary>SHA-256 of the per-page row sets of the reading ("page:seq,seq;...").</summary>
    public string? RowSetsSha256 { get; set; }

    public int RowsRead { get; set; }

    public int CharacterRowsPassed { get; set; }

    public List<CharacterResult> CharacterFailures { get; set; } = [];

    public List<PageGeometry> Geometry { get; set; } = [];

    public int GeometryOtherPages { get; set; }

    public int? FirstOtherPage { get; set; }

    /// <summary>Pages holding a column-header row (the text oracle's classification).</summary>
    public List<int> HeaderRowPages { get; set; } = [];

    public string? TextVerdict { get; set; }

    public int TextFailureCount { get; set; }

    public List<TextFailure> TextFailures { get; set; } = [];

    public string? ContentSha256 { get; set; }

    public List<RowSpan> MultiPageRows { get; set; } = [];

    public bool LayoutRepeated { get; set; }

    public bool? SemPass { get; set; }

    public string? SemInnerSha256 { get; set; }

    public long? HtmlBytes { get; set; }

    public JsonObject Measurements { get; set; } = [];

    /// <summary>The layout library's own rule lines, as the harness recorded them from the page (reported beside, never a verdict).</summary>
    public string? LibraryRules { get; set; }

    public double CheckSeconds { get; set; }
}

internal sealed record RowSpan(int Row, int FirstPage, int LastPage);

/// <summary>The "account" command: the strict check, the character check and the geometry observation of every planned run.</summary>
internal static class Account
{
    public static readonly string[] TextClasses = ["text-dropped", "text-repeated", "text-altered"];

    public static async Task<int> RunAsync(string fixturesDirectory, IReadOnlyList<string> runsDirectories, IReadOnlyList<string> integrityPaths, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var started = DateTimeOffset.Now;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var fixtures = new Dictionary<string, Fixture>(StringComparer.Ordinal);
        var integrity = new Dictionary<string, IntegrityEntry>(StringComparer.Ordinal);
        foreach (var path in integrityPaths)
        {
            foreach (var (id, entry) in Files.ReadIntegrity(path))
            {
                integrity[id] = entry;
            }
        }

        var entries = new List<StrictEntry>();
        foreach (var directory in runsDirectories)
        {
            var stage = StageFolder.Load(directory);
            foreach (var planned in stage.Plan.Runs)
            {
                var run = stage.Runs.GetValueOrDefault(planned.RunId);
                var entry = await EvaluateAsync(planned, run, stage, Fixture(planned.FixtureId), integrity.GetValueOrDefault(planned.RunId), timeout, cancellation);
                entries.Add(entry);
                Console.WriteLine($"{entry.Run}: {entry.RunClass} {entry.Verdict}, pages {entry.Pages}, accounting {entry.AccountingVerdict}, "
                    + $"characters {entry.CharacterRowsPassed}/{entry.RowsRead + entry.CharacterFailures.Count(f => f.Class == "row-lost")} pass, geometry other {entry.GeometryOtherPages}, text {entry.TextVerdict}");
            }
        }

        var checker = new JsonObject
        {
            ["poppler"] = await PopplerVersionAsync(cancellation),
            ["startedAt"] = started.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["endedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["timeoutSeconds"] = timeoutSeconds,
            ["fixtureSumsSha256"] = Sums.HashFile(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName)),
        };
        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "accounting.json"), new { checker, runs = entries });
        Console.WriteLine($"{entries.Count} entries: {entries.Count(e => e.Verdict == "checked")} checked, {entries.Count(e => e.Verdict == "no-pdf")} no-pdf, {entries.Count(e => e.Verdict == "no-check")} no-check.");
        return 0;

        Fixture Fixture(string id)
        {
            if (!fixtures.TryGetValue(id, out var fixture))
            {
                fixture = FixtureFile.ReadVerified(fixturesDirectory, id, sums);
                fixtures[id] = fixture;
            }

            return fixture;
        }
    }

    public static async Task<StrictEntry> EvaluateAsync(HarnessPlanned planned, HarnessRun? run, StageFolder stage, Fixture fixture, IntegrityEntry? text, TimeSpan timeout, CancellationToken cancellation)
    {
        var entry = new StrictEntry
        {
            Run = planned.RunId,
            Stage = planned.Stage,
            Markup = planned.Markup,
            FixtureId = planned.FixtureId,
            Mutation = planned.Mutation,
            Repetition = planned.Repetition,
            MatrixRow = planned.MatrixRow,
            Split = planned.Split,
            Trigger = planned.Trigger,
            PartLimit = planned.PartLimit,
            FooterOn = planned.FooterOn,
            RunClass = run?.Outcome ?? "missing",
            RunReason = run?.Reason,
            TextVerdict = text?.Verdict,
            TextFailureCount = text?.FailureCount ?? 0,
            ContentSha256 = text?.ContentSha256,
            AccountingVerdict = "n/a",
        };
        if (run is not null)
        {
            entry.LayoutRepeated = run.LayoutRepeated;
            entry.SemPass = run.Outcome == "ok" ? run.SemPass : null;
            entry.SemInnerSha256 = run.SemInnerSha256;
            entry.HtmlBytes = run.HtmlBytes;
            entry.LibraryRules = run.OwnRules;
            entry.Measurements = new JsonObject
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

        var pdf = run?.Pdf is null ? null : Path.Combine(stage.Directory, run.Pdf);
        if (run is null || run.Outcome != "ok" || pdf is null || !File.Exists(pdf))
        {
            entry.Verdict = "no-pdf";
            entry.Reason = run is null ? "no run record" : run.Outcome + (run.Reason is null ? string.Empty : $" ({run.Reason})");
            return entry;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var reading = await ReadAsync(pdf, fixture, planned.FooterOn, timeout, cancellation);
            Judge(entry, reading, fixture, run, text);

            // Rule 7: this footer-on run against its footer-off twin (same fixture, split and values), when planned.
            if (planned.FooterOn)
            {
                var twin = stage.Plan.Runs.FirstOrDefault(p => !p.FooterOn && p.FixtureId == planned.FixtureId && p.Mutation == planned.Mutation
                    && p.Split == planned.Split && p.Trigger == planned.Trigger && p.PartLimit == planned.PartLimit && p.Repetition == planned.Repetition);
                if (twin is not null && stage.Runs.GetValueOrDefault(twin.RunId) is { Outcome: "ok", Pdf: not null } twinRun)
                {
                    var off = await ReadAsync(Path.Combine(stage.Directory, twinRun.Pdf), fixture, false, timeout, cancellation);
                    var check = AccountingVerifier.CompareLayouts(AccountingVerifier.FooterNoDisplacement, off.ToLayout(), reading.ToLayout());
                    entry.Rule7 = new RuleLine(7, check.Name, check.Outcome, check.FirstFailingPage, check.Detail);
                    entry.Rule7Pair = twin.RunId;
                    if (!check.Pass)
                    {
                        entry.FailureSet.Add($"{check.Name}|p{check.FirstFailingPage}");
                    }
                }
                else if (twin is not null)
                {
                    entry.Rule7Pair = twin.RunId;
                    entry.Rule7 = new RuleLine(7, AccountingVerifier.FooterNoDisplacement, "NOT EVALUATED", null, "the footer-off twin has no PDF");
                }
            }

            entry.Verdict = "checked";
        }
        catch (Exception e) when (e is TimeoutException or InvalidDataException or IOException or System.Xml.XmlException or System.ComponentModel.Win32Exception or FormatException or InvalidOperationException or OutOfMemoryException)
        {
            entry.Verdict = "no-check";
            entry.Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
        }

        entry.CheckSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
        return entry;
    }

    public static async Task<PdfReading> ReadAsync(string pdf, Fixture fixture, bool footerOn, TimeSpan timeout, CancellationToken cancellation) =>
        await PdfReading.ReadAsync(pdf, fixture, footerOn ? "expected" : "none", footerOn ? "accounting" : "none", timeout, cancellation);

    /// <summary>Everything this checker says about one printed reading: rules 1-9, the full evaluation, characters, geometry and the text failures.</summary>
    public static void Judge(StrictEntry entry, PdfReading reading, Fixture fixture, HarnessRun? run, IntegrityEntry? text)
    {
        var block = PdfReading.ToBlock(fixture);
        var layout = reading.ToLayout();
        entry.Pages = reading.Pages;
        var result = new RunResult
        {
            Id = entry.Run,
            Configuration = $"{entry.Markup}-presplit",
            Block = fixture.Id,
            Footer = entry.FooterOn,
            Repetition = entry.Repetition,
            Purpose = "strict",
            Status = "ok",
            Dialogs = run?.Dialogs ?? 0,
            AbortedRequests = run?.AbortedRequests ?? 0,
            NotFoundRequests = run?.NotFoundRequests ?? 0,
            ScriptsExpected = 0,
            ScriptsFound = run is null ? 0 : run.Scripts,
            Layout = layout,
        };
        var checks = AccountingVerifier.VerifyPaged(block, result);
        entry.Rules = [.. checks.Select(c => new RuleLine(RuleNumber(c.Name), c.Name, c.Outcome, c.FirstFailingPage, c.Detail))];
        entry.FailureSet = [.. checks.Where(c => !c.Pass && !Observation(c.Name)).Select(c => $"{c.Name}|p{c.FirstFailingPage}")];
        if (checks.Any(c => !c.Pass && c.Name is AccountingVerifier.Rows or AccountingVerifier.VisibleAmounts or AccountingVerifier.Marks))
        {
            entry.FullEvaluation = Evaluation.FullEvaluation(block, layout, reading);
        }

        if (entry.FooterOn)
        {
            var failing = checks.FirstOrDefault(c => !c.Pass && c.Name is AccountingVerifier.Rows or AccountingVerifier.EndPageSubtotals or AccountingVerifier.Chain);
            entry.AccountingVerdict = failing is null ? "exact" : $"{failing.Name} p{failing.FirstFailingPage}: {failing.Detail}";
        }

        var rowSets = new StringBuilder();
        foreach (var page in layout.Pages)
        {
            rowSets.Append(page.Page).Append(':').AppendJoin(',', page.Fragments.Select(f => f.Seq)).Append(';');
        }

        entry.RowSetsSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rowSets.ToString())));
        entry.RowsRead = reading.RowPages.Count;
        entry.MultiPageRows = [.. reading.RowPages.Where(kv => kv.Value.Max > kv.Value.Min).OrderBy(kv => kv.Key).Select(kv => new RowSpan(kv.Key, kv.Value.Min, kv.Value.Max))];

        var characters = CharacterCheck.Check(reading, fixture);
        entry.CharacterRowsPassed = characters.Count(c => c.Pass);
        entry.CharacterFailures = [.. characters.Where(c => !c.Pass)];

        entry.Geometry = ColumnGeometry.Observe(reading, fixture);
        entry.GeometryOtherPages = entry.Geometry.Count(g => g.Class == "other");
        entry.FirstOtherPage = entry.Geometry.FirstOrDefault(g => g.Class == "other")?.Page;
        var lines = reading.Document.ContentLines;
        entry.HeaderRowPages = [.. lines.Where(reading.Rules.IsHeaderRow).Select(l => l.Page).Distinct().Order()];

        var other = entry.Geometry.Where(g => g.Class == "other").Select(g => g.Page).ToHashSet();
        foreach (var f in text?.Failures ?? [])
        {
            var pages = f.Page is { } p ? [p] : f.Row is { } row && reading.RowPages.TryGetValue(row, out var rowPages) ? rowPages.ToList() : [];
            var kind = (f.Rule == "TI-3" && f.Field is "description" or "note") || (f.Rule == "TI-4" && f.Field == "date");
            var candidate = entry.Markup == "table" && kind && TextClasses.Contains(f.Class) && pages.Count > 0 && pages.All(other.Contains);
            entry.TextFailures.Add(new TextFailure(f.Rule, f.Class, f.Row, f.Field, f.Region, f.Page, f.Detail, candidate));
        }
    }

    public static int RuleNumber(string name) => name switch
    {
        AccountingVerifier.Rows => 1,
        AccountingVerifier.EndPageSubtotals => 2,
        AccountingVerifier.Chain => 3,
        AccountingVerifier.VisibleAmounts => 4,
        AccountingVerifier.Marks or AccountingVerifier.SplitAttributes => 5,
        AccountingVerifier.Geometry => 6,
        AccountingVerifier.FooterNoDisplacement => 7,
        AccountingVerifier.Escaping => 8,
        _ => 9,
    };

    public static bool Observation(string name) =>
        name is AccountingVerifier.ColumnAlignment or AccountingVerifier.DescriptionText or AccountingVerifier.SplitRows;

    public static async Task<string> PopplerVersionAsync(CancellationToken cancellation)
    {
        try
        {
            var result = await Tool.RunAsync("pdftotext", ["-v"], TimeSpan.FromSeconds(30), cancellation);
            return (result.StandardError + result.StandardOutput).Split('\n')[0].Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return "unknown";
        }
    }
}
