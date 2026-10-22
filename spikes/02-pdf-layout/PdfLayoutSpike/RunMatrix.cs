using System.Globalization;

namespace PdfLayoutSpike;

/// <summary>The calibration of one markup: how much description text fills one content area.</summary>
internal sealed record CalibrationResult(
    string Configuration,
    string RunId,
    int Sequence,
    int DescriptionLength,
    string Status,
    int? Fragments,
    int? Page,
    double? RowHeightPx,
    double? AreaHeightPx,
    double? CharactersPerPage,
    bool Valid,
    string Note);

/// <summary>One tall-row run: its target, its actual row and what the layout did with it.</summary>
internal sealed record TallRowResult(
    string RunId,
    string Configuration,
    string Fixture,
    double K,
    double CharactersPerPage,
    int TargetLength,
    int ActualLength,
    string Status,
    IReadOnlyList<int> SpanPages,
    int SpanCount,
    int CharactersShown,
    string OutcomeClass,
    string ClassDetail,
    IReadOnlyList<string> RuleLines,
    IReadOnlyList<string> ConsoleMessages,
    SignatureEvaluation? Signature);

/// <summary>The overall verdict: the exit code, each reason for a failure, and notes.</summary>
internal sealed record Verdict(int ExitCode, IReadOnlyList<string> Failures, IReadOnlyList<string> Notes);

/// <summary>
/// The run matrix: accounting and displacement runs of the four pagination
/// configurations on the ordinary block and on the giant-row fixture, the
/// calibration and tall-row series, the memory series of the primary
/// configuration and of grid + keep, the determinism run and the native-printing
/// comparison; then the verdict.
/// </summary>
internal sealed class RunMatrix(BlockSource source, BlockHtmlBuilder builder, LayoutRunner runner, string outDirectory, int timeoutSeconds)
{
    /// <summary>Primary first, then the fallback order.</summary>
    public static readonly RunConfiguration[] PagedOrder =
    [
        new(LayoutMode.Paged, RowMarkup.Table, KeepPolicy.Keep),
        new(LayoutMode.Paged, RowMarkup.Grid, KeepPolicy.Keep),
        new(LayoutMode.Paged, RowMarkup.Table, KeepPolicy.Split),
        new(LayoutMode.Paged, RowMarkup.Grid, KeepPolicy.Split),
    ];

    public static readonly RunConfiguration NativeTable = new(LayoutMode.Native, RowMarkup.Table, KeepPolicy.Split);

    private static readonly RunConfiguration TableKeep = PagedOrder[0];

    private static readonly RunConfiguration GridKeep = PagedOrder[1];

    private static readonly int[] MemorySizes = [250, 500, 1000, 2000, 4000];

    private static readonly double[] TallRowMultiples = [1.5, 2, 3, 5];

    public List<RunResult> Runs { get; } = [];

    public List<CheckResult> PairChecks { get; } = [];

    public CheckResult? DeterminismCheck { get; private set; }

    public RunConfiguration Primary { get; private set; } = TableKeep;

    public string PrimaryNote { get; private set; } = "table + keep (default primary)";

    public bool PrimaryPassesAccounting { get; private set; }

    public string? BestOtherConfiguration { get; private set; }

    public List<string> MemorySeries { get; } = [];

    public List<CalibrationResult> Calibrations { get; } = [];

    public List<TallRowResult> TallRows { get; } = [];

    public List<SignatureEvaluation> GiantRowSignatures { get; } = [];

    public List<string> Skipped { get; } = [];

    public Verdict? Verdict { get; private set; }

    private bool Full { get; set; }

    public async Task ExecuteAsync(bool full)
    {
        Full = full;
        var giant = source.GiantRows();
        if (!full)
        {
            await PairAsync(Primary, source.Slice("B250", 250), "quick");
            await PairAsync(Primary, giant, "quick");
            EvaluateGiantRow(giant);
            PrimaryPassesAccounting = PassesSelection(Primary, "B250");
            Verdict = ComputeVerdict();
            return;
        }

        var b1000 = source.Slice("B1000", 1000);
        foreach (var config in PagedOrder)
        {
            await PairAsync(config, b1000, "accounting");
            await PairAsync(config, giant, "limit");
        }

        EvaluateGiantRow(giant);
        SelectPrimary();

        await CalibrateAndBracketAsync();

        // Memory series: the primary configuration and grid + keep (one series if they are the same).
        foreach (var config in new[] { Primary, GridKeep }.Distinct())
        {
            MemorySeries.Add(config.Name);
            await MemorySeriesAsync(config);
        }

        // Determinism: one more B1000 run of the primary, compared with its first memory run.
        var reference = Runs.FirstOrDefault(r => r.Purpose == "memory" && r.Configuration == Primary.Name && r.Block == "B1000" && r.Repetition == 1);
        var again = await RunOneAsync(Primary, b1000, footer: true, 4, "determinism");
        DeterminismCheck = reference?.Layout is not null && again.Layout is not null
            ? AccountingVerifier.CompareLayouts(AccountingVerifier.Determinism, reference.Layout, again.Layout)
            : new CheckResult(AccountingVerifier.Determinism, false, null, "a run of the pair did not finish");
        Report(DeterminismCheck, "B1000 memory run 1 vs determinism run");

        // Native printing, same header and counter CSS.
        for (var repetition = 1; repetition <= 3; repetition++)
        {
            await RunOneAsync(NativeTable, b1000, footer: false, repetition, "native");
        }

        Verdict = ComputeVerdict();
    }

    /// <summary>The exit code of the verdict (1 when no verdict was computed).</summary>
    public int ExitCode() => Verdict?.ExitCode ?? 1;

    /// <summary>Prints the verdict and each reason for it.</summary>
    public void PrintVerdict()
    {
        if (Verdict is null)
        {
            Console.WriteLine("Verdict: none computed — exit 1.");
            return;
        }

        foreach (var note in Verdict.Notes)
        {
            Console.WriteLine($"  note: {note}");
        }

        foreach (var failure in Verdict.Failures)
        {
            Console.WriteLine($"  failure: {failure}");
        }

        Console.WriteLine(Verdict.ExitCode == 0
            ? "Verdict: exit 0 — every ordinary check passes and the giant-row fixture shows exactly the documented failure signature in every configuration run."
            : $"Verdict: exit 1 — {Verdict.Failures.Count} reason(s) above.");
    }

    /// <summary>
    /// The primary is chosen on the ordinary block shared by all four configurations:
    /// rules 1 to 5 on its footer-on run, and no "Layout repeated" message on that run.
    /// The giant-row fixture is not part of the selection.
    /// </summary>
    private void SelectPrimary()
    {
        var passing = PagedOrder.FirstOrDefault(c => PassesSelection(c, "B1000"));
        if (passing is null)
        {
            Primary = TableKeep;
            PrimaryPassesAccounting = false;
            var failing = string.Join("; ", PagedOrder.Select(c => $"{c.Name}: {FirstSelectionFailure(c, "B1000")}"));
            PrimaryNote = $"no configuration passes the page-subtotal checks on B1000 ({failing}); the primary stays table + keep";
        }
        else
        {
            PrimaryPassesAccounting = true;
            PrimaryNote = passing == TableKeep
                ? "table + keep passes rules 1 to 5 on B1000"
                : $"switched from table + keep to {passing.Name} because table + keep failed on B1000: {FirstSelectionFailure(TableKeep, "B1000")}";
            Primary = passing;
        }

        BestOtherConfiguration = Primary == TableKeep ? GridKeep.Name : TableKeep.Name;
    }

    private bool PassesSelection(RunConfiguration config, string block)
    {
        var run = Runs.FirstOrDefault(r => r.Configuration == config.Name && r.Block == block && r.Footer);
        return run is not null && AccountingVerifier.PassesEndPageRules(run) && !AccountingVerifier.LogsLayoutRepeated(run);
    }

    private string FirstSelectionFailure(RunConfiguration config, string block)
    {
        var run = Runs.FirstOrDefault(r => r.Configuration == config.Name && r.Block == block && r.Footer);
        if (run is null)
        {
            return "not run";
        }

        if (run.Status != "ok")
        {
            return $"status {run.Status}";
        }

        var failed = run.Checks.FirstOrDefault(c => !c.Pass && (AccountingVerifier.AccountingChecks.Contains(c.Name) || c.Name == AccountingVerifier.SplitAttributes));
        if (failed is not null)
        {
            return $"{failed.Name} FAIL{(failed.FirstFailingPage is int p ? $" page {p}" : string.Empty)} ({failed.Detail})";
        }

        return AccountingVerifier.LogsLayoutRepeated(run) ? "console 'Layout repeated'" : "passes";
    }

    private void EvaluateGiantRow(Block giant)
    {
        foreach (var run in Runs.Where(r => r.Block == giant.Name && r.Footer))
        {
            var evaluation = AccountingVerifier.EvaluateSignature(giant, run, BlockSource.TallRowSequence);
            run.OutcomeClass = evaluation.Signature ? "signature" : "different";
            GiantRowSignatures.Add(evaluation);
            Console.WriteLine($"  {run.OutcomeClass.ToUpperInvariant()} {run.Configuration} G200 signature: S1 {Mark(evaluation.S1)} ({evaluation.S1Evidence}); S2 {Mark(evaluation.S2)} ({evaluation.S2Evidence}); S3 {Mark(evaluation.S3)} ({evaluation.S3Evidence})");
        }
    }

    /// <summary>
    /// Calibration on one natural row per markup, then the tall-row fixtures derived
    /// from it. The fixtures are characterisation only: their classes never set the
    /// exit code.
    /// </summary>
    private async Task CalibrateAndBracketAsync()
    {
        var calibration = source.Calibration();
        var record = calibration.Records[0];
        foreach (var config in new[] { TableKeep, GridKeep })
        {
            var run = await RunOneAsync(config, calibration, footer: false, 1, "calibration");
            var result = Calibrate(config, run, record);
            Calibrations.Add(result);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  calibration {config.Name}: sequence {result.Sequence}, {result.DescriptionLength} characters, {result.Fragments?.ToString(CultureInfo.InvariantCulture) ?? "-"} fragment(s), row {result.RowHeightPx:0.0} px, content area {result.AreaHeightPx:0.0} px, c = {result.CharactersPerPage:0.0} — {result.Note}"));
        }

        foreach (var config in new[] { TableKeep, GridKeep })
        {
            var cal = Calibrations.First(c => c.Configuration == config.Name);
            foreach (var k in TallRowMultiples)
            {
                var name = "T" + (k * 100).ToString("0", CultureInfo.InvariantCulture);
                if (!cal.Valid || cal.CharactersPerPage is not double c)
                {
                    Skipped.Add($"{config.Name} {name}: not run — {cal.Note}");
                    continue;
                }

                var target = (int)Math.Ceiling(k * c);
                var block = source.TallRow(name, target);
                var tall = block.Records.First(r => r.Sequence == BlockSource.TallRowSequence);
                var run = await RunOneAsync(config, block, footer: true, 1, "bracketing");
                TallRows.Add(Classify(config, block, run, k, c, target, tall.Description.Length));
            }
        }
    }

    private static CalibrationResult Calibrate(RunConfiguration config, RunResult run, DatasetRecord record)
    {
        if (run.Status != "ok" || run.Layout is null)
        {
            return new CalibrationResult(config.Name, run.Id, record.Sequence, record.Description.Length, run.Status, null, null, null, null, null, false, $"calibration run status {run.Status}; the tall-row runs of this markup are not run");
        }

        var fragments = run.Layout.Pages
            .SelectMany(p => p.Fragments.Where(f => f.Seq == record.Sequence).Select(f => (Page: p, Fragment: f)))
            .ToList();
        if (fragments.Count != 1)
        {
            return new CalibrationResult(config.Name, run.Id, record.Sequence, record.Description.Length, run.Status, fragments.Count, null, null, null, null, false, $"the calibration row lays out as {fragments.Count} fragments, so c is not valid; no correction is made and the tall-row runs of this markup are not run");
        }

        var (page, fragment) = fragments[0];
        var height = fragment.Bottom - fragment.Top;
        if (height is not > 0 || page.AreaHeight is not > 0)
        {
            return new CalibrationResult(config.Name, run.Id, record.Sequence, record.Description.Length, run.Status, 1, page.Page, height, page.AreaHeight, null, false, "row or content-area height not measured; the tall-row runs of this markup are not run");
        }

        var c = record.Description.Length * page.AreaHeight.Value / height.Value;
        return new CalibrationResult(config.Name, run.Id, record.Sequence, record.Description.Length, run.Status, 1, page.Page, height, page.AreaHeight, c, true, "single fragment; c = description length × content-area height ÷ row height");
    }

    private TallRowResult Classify(RunConfiguration config, Block block, RunResult run, double k, double c, int target, int actual)
    {
        var spanPages = run.Layout is null
            ? []
            : AccountingVerifier.PagesOfSequence(run.Layout).GetValueOrDefault(BlockSource.TallRowSequence, []).Distinct().ToList();
        var shown = run.Layout?.Pages.SelectMany(p => p.Fragments).Where(f => f.Seq == BlockSource.TallRowSequence).Sum(f => f.DescChars) ?? 0;
        SignatureEvaluation? signature = null;
        string outcome;
        string detail;
        if (run.Status == "ok" && AccountingVerifier.PassesEndPageRules(run) && !AccountingVerifier.LogsLayoutRepeated(run))
        {
            outcome = "pass";
            detail = "rules 1 to 5 pass and no 'Layout repeated' message";
        }
        else
        {
            signature = AccountingVerifier.EvaluateSignature(block, run, BlockSource.TallRowSequence);
            if (signature.Signature)
            {
                outcome = "signature";
                detail = "S1 to S3 hold with the tall record in place of record 50";
            }
            else
            {
                outcome = "other";
                var rule1 = run.Checks.FirstOrDefault(ch => ch.Name == AccountingVerifier.Rows);
                var failed = run.Checks.Where(ch => !ch.Pass).Select(ch => ch.Name).ToList();
                detail = run.Status != "ok"
                    ? $"status {run.Status}{(run.Message is null ? string.Empty : ": " + run.Message)}"
                    : $"rule 1 {rule1?.Outcome} ({rule1?.Detail}); failed: {(failed.Count == 0 ? "none" : string.Join(", ", failed))}; 'Layout repeated' {(AccountingVerifier.LogsLayoutRepeated(run) ? "logged" : "not logged")}; S1 {Mark(signature.S1)}, S2 {Mark(signature.S2)}, S3 {Mark(signature.S3)}";
            }
        }

        run.OutcomeClass = outcome;
        run.TargetK = k;
        run.TallRowLength = actual;
        run.ObservedSpan = spanPages.Count;
        var result = new TallRowResult(
            run.Id,
            config.Name,
            block.Name,
            k,
            c,
            target,
            actual,
            run.Status,
            spanPages,
            spanPages.Count,
            shown,
            outcome,
            detail,
            run.Checks.Select(ch => $"{ch.Outcome} {ch.Name}{(ch.FirstFailingPage is int p ? $" (page {p})" : string.Empty)}: {ch.Detail}").ToList(),
            run.ConsoleMessages.ToList(),
            signature);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {outcome.ToUpperInvariant()} {config.Name} {block.Name}: k {k}, c {c:0.0}, target {target}, length {actual}, shown {shown}, span {spanPages.Count} page(s) [{string.Join(',', spanPages)}] — {detail}"));
        return result;
    }

    private async Task MemorySeriesAsync(RunConfiguration config)
    {
        var timedOut = false;
        foreach (var size in MemorySizes)
        {
            var block = source.Slice($"B{size}", size);
            for (var repetition = 1; repetition <= 3; repetition++)
            {
                if (timedOut)
                {
                    Skipped.Add($"{config.Name} {block.Name} repetition {repetition} (a smaller or equal size of this series timed out)");
                    continue;
                }

                var run = await RunOneAsync(config, block, footer: true, repetition, "memory");
                timedOut = run.Status == "layout-timeout";
            }
        }
    }

    /// <summary>
    /// The verdict over the whole matrix. Ordinary runs (the B blocks and the native
    /// runs) must pass every check and log no "Layout repeated"; each configuration's
    /// giant-row run must show exactly the documented signature, with displacement
    /// and escaping passing; characterisation runs may not fail escaping or error.
    /// </summary>
    private Verdict ComputeVerdict()
    {
        var failures = new List<string>();
        var notes = new List<string>();

        foreach (var run in Runs.Where(IsOrdinary))
        {
            if (run.Status != "ok")
            {
                if (run.Purpose == "memory" && run.Status == "layout-timeout")
                {
                    notes.Add($"{run.Id}: timed out — a measured limit of its memory series; larger sizes skipped");
                }
                else
                {
                    failures.Add($"{run.Id}: status {run.Status}{(run.Message is null ? string.Empty : " — " + run.Message)}");
                }
            }

            foreach (var check in run.Checks.Where(c => !c.Pass))
            {
                failures.Add($"{run.Id}: {check.Name} FAIL{(check.FirstFailingPage is int p ? $" (page {p})" : string.Empty)} — {check.Detail}");
            }

            if (AccountingVerifier.LogsLayoutRepeated(run))
            {
                failures.Add($"{run.Id}: an ordinary block logged '{AccountingVerifier.LayoutRepeated}' on the console");
            }
        }

        foreach (var pair in PairChecks.Where(p => !p.Pass))
        {
            failures.Add($"{pair.Name}: FAIL{(pair.FirstFailingPage is int p ? $" (page {p})" : string.Empty)} — {pair.Detail}");
        }

        if (Full && DeterminismCheck is not { Pass: true })
        {
            failures.Add($"{AccountingVerifier.Determinism}: {DeterminismCheck?.Detail ?? "not run"}");
        }

        foreach (var evaluation in GiantRowSignatures.Where(e => !e.Signature))
        {
            failures.Add($"G200 {evaluation.Configuration}: different from the documented signature — S1 {Mark(evaluation.S1)} ({evaluation.S1Evidence}); S2 {Mark(evaluation.S2)} ({evaluation.S2Evidence}); S3 {Mark(evaluation.S3)} ({evaluation.S3Evidence})");
        }

        foreach (var run in Runs.Where(r => r.Block == "G200"))
        {
            foreach (var check in run.Checks.Where(c => !c.Pass && c.Name is AccountingVerifier.Geometry or AccountingVerifier.Escaping))
            {
                failures.Add($"{run.Id}: {check.Name} FAIL — {check.Detail}");
            }

            if (run.Status == "ok" && !run.Checks.Any(c => c.Name == AccountingVerifier.Escaping))
            {
                failures.Add($"{run.Id}: escaping not checked");
            }
        }

        var expectedGiantRuns = Full ? PagedOrder.Length : 1;
        if (GiantRowSignatures.Count != expectedGiantRuns)
        {
            failures.Add($"G200: {GiantRowSignatures.Count} footer-on runs evaluated, {expectedGiantRuns} expected");
        }

        foreach (var run in Runs.Where(r => r.Purpose is "calibration" or "bracketing"))
        {
            if (run.Status == "error")
            {
                failures.Add($"{run.Id}: run error — {run.Message}");
            }
            else if (run.Purpose == "calibration" && run.Status != "ok")
            {
                failures.Add($"{run.Id}: calibration run status {run.Status} — {run.Message}");
            }

            foreach (var check in run.Checks.Where(c => !c.Pass && c.Name == AccountingVerifier.Escaping))
            {
                failures.Add($"{run.Id}: {check.Name} FAIL — {check.Detail}");
            }

            if (run.Status != "ok")
            {
                notes.Add($"{run.Id}: status {run.Status}; escaping not evaluated (no layout result)");
            }
        }

        foreach (var cal in Calibrations.Where(c => !c.Valid))
        {
            notes.Add($"calibration {cal.Configuration}: {cal.Note}");
        }

        if (Full && !PrimaryPassesAccounting)
        {
            failures.Add($"no configuration passes the page-subtotal checks on B1000 — {PrimaryNote}");
        }

        notes.AddRange(TallRows.Select(t => $"{t.Configuration} {t.Fixture}: {t.OutcomeClass} (characterisation, never sets the exit code)"));
        return new Verdict(failures.Count == 0 ? 0 : 1, failures, notes);
    }

    private static bool IsOrdinary(RunResult run) => run.Block.StartsWith('B');

    private async Task PairAsync(RunConfiguration config, Block block, string purpose)
    {
        var on = await RunOneAsync(config, block, footer: true, 1, purpose);
        var off = await RunOneAsync(config, block, footer: false, 1, purpose);
        var name = $"{config.Name} {block.Name} {AccountingVerifier.FooterNoDisplacement}";
        var check = on.Layout is not null && off.Layout is not null && on.Status == "ok" && off.Status == "ok"
            ? AccountingVerifier.CompareLayouts(name, on.Layout, off.Layout)
            : new CheckResult(name, false, null, "a run of the pair did not finish");
        PairChecks.Add(check);
        Report(check, "footer on vs off");
    }

    private async Task<RunResult> RunOneAsync(RunConfiguration config, Block block, bool footer, int repetition, string purpose)
    {
        // Run ids are unique within a matrix: the memory, determinism, calibration and
        // tall-row runs carry their purpose, so no run overwrites another's files.
        var suffix = purpose is "memory" or "determinism" or "calibration" or "bracketing" ? "-" + purpose : string.Empty;
        var id = config.Mode == LayoutMode.Native
            ? $"{config.Name}-{block.Name}-r{repetition}"
            : $"{config.Name}-{block.Name}-{(footer ? "on" : "off")}{suffix}-r{repetition}";
        var run = new RunResult
        {
            Id = id,
            Configuration = config.Name,
            Block = block.Name,
            Footer = footer,
            Repetition = repetition,
            Purpose = purpose,
        };
        var (html, scripts) = builder.Build(source, block, config, footer, id);
        run.ScriptsExpected = scripts;

        Func<LayoutResult, IEnumerable<int>>? images = null;
        if (config.Mode == LayoutMode.Paged && block.Name == "G200" && footer)
        {
            images = GiantRowPages;
        }

        try
        {
            await runner.RunAsync(run, html, config.Mode, outDirectory, timeoutSeconds, images);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            run.Status = "error";
            run.Message = e.GetType().Name + ": " + e.Message.Split('\n')[0];
        }

        if (run.Status == "ok")
        {
            run.Checks = config.Mode == LayoutMode.Paged ? AccountingVerifier.VerifyPaged(block, run) : AccountingVerifier.VerifyNative(run);
        }

        Runs.Add(run);
        var memory = run.Memory;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Id,-44} {run.Status,-14} pages={run.Pages?.ToString(CultureInfo.InvariantCulture) ?? "-",-5} layout={Ms(run.LayoutMs)} pdf={Ms(run.PdfMs)} chromiumPeakRss={memory?.ChromiumTreePeakRssKiB ?? 0}KiB"));
        foreach (var check in run.Checks)
        {
            Console.WriteLine($"    {check.Outcome} {check.Name}{(check.FirstFailingPage is int p ? $" (page {p})" : string.Empty)}: {check.Detail}");
        }

        if (run.Message is not null)
        {
            Console.WriteLine($"    {run.Message}");
        }

        foreach (var message in run.ConsoleMessages.Take(3))
        {
            Console.WriteLine($"    console {message[..Math.Min(message.Length, 160)]}");
        }

        return run;
    }

    /// <summary>Page 1 (a normal page with the footer) and the start, middle and end pages of the two giant rows.</summary>
    private static IEnumerable<int> GiantRowPages(LayoutResult layout)
    {
        yield return 1;
        foreach (var seq in new[] { 50, 120 })
        {
            var pages = layout.Pages.Where(p => p.Fragments.Any(f => f.Seq == seq)).Select(p => p.Page).ToList();
            if (pages.Count == 0)
            {
                continue;
            }

            yield return pages[0];
            yield return pages[pages.Count / 2];
            yield return pages[^1];
        }
    }

    private static string Mark(bool value) => value ? "holds" : "does not hold";

    private static string Ms(double? value) => value is double v ? v.ToString("0", CultureInfo.InvariantCulture) + "ms" : "-";

    private static void Report(CheckResult check, string what) =>
        Console.WriteLine($"  {check.Outcome} {check.Name} [{what}]{(check.FirstFailingPage is int p ? $" (page {p})" : string.Empty)}: {check.Detail}");
}
