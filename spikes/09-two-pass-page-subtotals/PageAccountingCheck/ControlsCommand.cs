using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PaginationFixtures;
using PdfLayoutSpike;
using TextIntegrityCheck;

namespace PageAccountingCheck;

/// <summary>One control of the accounting checker: what was changed, what must happen, what happened.</summary>
internal sealed class ControlResult
{
    public required string Id { get; init; }

    public required string Base { get; init; }

    public required string Change { get; init; }

    public required string Expected { get; init; }

    /// <summary>"yes" | "no" | "not evaluable".</summary>
    public string Behaved { get; set; } = "not evaluable";

    /// <summary>Counted controls decide the verdict; recorded ones are evidence only.</summary>
    public bool Counted { get; init; } = true;

    public List<string> Observed { get; set; } = [];

    public string? Detail { get; set; }
}

/// <summary>
/// The "controls" command: the page-box controls BX-1 and BX-2, the extraction-level
/// controls XC-1 and XC-2 (in memory, on a parsed pass-2 document), and one recorded
/// observation, RB-3-swap: the first definition of the order fault (the labels of rows
/// 10 and 11 exchanged), kept to show why that fault fails two checks.
/// </summary>
internal static partial class ControlsCommand
{

    public static async Task<int> RunAsync(string fixturesDirectory, string runsDirectory, string outDirectory, string workDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        Directory.CreateDirectory(workDirectory);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var runs = Files.ReadTwoPass(runsDirectory);
        var results = new List<ControlResult>();

        // The base runs are strategy "stamped" runs, in the markup where that strategy runs (table when it may, else grid).
        var stampedMarkups = Files.ReadPlan(runsDirectory).StampedMarkups;
        var markup = stampedMarkups.Count == 0 || stampedMarkups.Contains("table") ? "table" : "grid";
        var pageBoxBase = $"B100-{markup}-s-r1";
        var marksBase = $"T300u-{markup}-s-r1";

        // BX-1 and BX-2.
        var bx1 = new ControlResult { Id = "BX-1", Base = pageBoxBase + ".p1", Change = "the rounded reference built from the pass-1 PDF", Expected = "every construction check holds" };
        var bx2 = new ControlResult { Id = "BX-2", Base = pageBoxBase + ".p2", Change = "a copy of the pass-2 PDF with the width of the first page box in the file byte-edited to 594.97", Expected = "box-differs on that page only" };
        results.Add(bx1);
        results.Add(bx2);
        if (runs.TryGetValue(pageBoxBase, out var boxRun) && boxRun.Pass1.Pdf is not null && boxRun.Pass2.Pdf is not null && boxRun.Pass2.Outcome == "ok")
        {
            var copy = Path.Combine(workDirectory, "bx1-ref-rounded.pdf");
            var rounded = await Qpdf.BuildRoundedAsync(Path.Combine(runsDirectory, boxRun.Pass1.Pdf), copy, workDirectory, timeout, cancellation);
            File.Delete(copy);
            bx1.Behaved = rounded.Pass ? "yes" : "no";
            bx1.Detail = rounded.Pass
                ? string.Create(CultureInfo.InvariantCulture, $"{rounded.Pages} pages, {rounded.DifferingBytes} differing bytes, all inside the box numbers; size {rounded.CopyBytes}; check {rounded.CopyCheck}; sha256 {rounded.CopySha256}")
                : rounded.Failure;

            var edited = Path.Combine(workDirectory, "bx2-edited.pdf");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(runsDirectory, boxRun.Pass2.Pdf), cancellation);
            var latin = Encoding.Latin1.GetString(bytes);
            var match = MediaBoxArray().Match(latin);
            var width = match.Success ? Width().Match(match.Groups[1].Value) : Match.Empty;
            if (!match.Success || !width.Success)
            {
                bx2.Detail = "no /MediaBox width number found";
            }
            else
            {
                var at = match.Groups[1].Index + width.Index;
                var original = width.Value;
                var replacement = original.Length == "594.96".Length ? "594.97" : "594.97" + new string('0', original.Length - "594.97".Length);
                Encoding.Latin1.GetBytes(replacement).CopyTo(bytes, at);
                await File.WriteAllBytesAsync(edited, bytes, cancellation);
                var pages = await Qpdf.PagesAsync(edited, workDirectory, timeout, cancellation);
                File.Delete(edited);
                var classes = pages.Select(p => (p.Page, Class: p.MediaBox is null ? Qpdf.Differs : Qpdf.Classify(p.MediaBox.Numbers))).ToList();
                var differing = classes.Where(c => c.Class == Qpdf.Differs).Select(c => c.Page).ToList();
                bx2.Observed = [.. differing.Select(p => $"box-differs p{p}")];
                bx2.Behaved = differing.Count == 1 ? "yes" : "no";
                bx2.Detail = $"'{original}' edited to '{replacement}' at byte {at}; {pages.Count} pages read";
            }
        }
        else
        {
            bx1.Detail = bx2.Detail = $"base run {pageBoxBase} has no pass-1 and pass-2 PDF";
        }

        // XC-1 and XC-2, on the parsed pass-2 document of the marks base.
        var xc1 = new ControlResult { Id = "XC-1", Base = marksBase + ".p2", Change = "every footer mark moved into the continuing row's amount band, same page", Expected = "rules 1–8 pass (either placement accepted)" };
        var xc2 = new ControlResult { Id = "XC-2", Base = marksBase + ".p2", Change = "on the first page with a mark, the mark kept in the footer and also printed in the row's amount band", Expected = "continues-marks fails on that page only" };
        results.Add(xc1);
        results.Add(xc2);
        if (runs.TryGetValue(marksBase, out var marksRun) && marksRun.Pass1.Pdf is not null && marksRun.Pass2.Pdf is not null && marksRun.Pass2.Outcome == "ok")
        {
            var fixture = FixtureFile.ReadVerified(fixturesDirectory, marksRun.FixtureId, sums);
            var block = PdfReading.ToBlock(fixture);
            var pass1 = await PdfReading.ReadAsync(Path.Combine(runsDirectory, marksRun.Pass1.Pdf), fixture, "none", "none", timeout, cancellation);
            var pass2 = await PdfReading.ReadAsync(Path.Combine(runsDirectory, marksRun.Pass2.Pdf), fixture, "expected", "accounting", timeout, cancellation);
            var layout1 = pass1.ToLayout();
            var markedPages = Enumerable.Range(1, pass2.Pages).Where(p => pass2.MarksOn(p).Footer > 0).ToList();

            var moved = pass2.Document.Clone();
            foreach (var page in markedPages)
            {
                MoveMark(moved, pass2, page, keepInFooter: false);
            }

            var xc1Checks = Verify(block, marksRun, PdfReading.From(moved, fixture, "expected", "accounting"), layout1);
            xc1.Observed = [.. xc1Checks.Where(c => !c.Pass).Select(c => $"{c.Name} p{c.FirstFailingPage}")];
            xc1.Behaved = markedPages.Count > 0 && xc1Checks.All(c => c.Pass) ? "yes" : "no";
            xc1.Detail = $"{markedPages.Count} marks moved: pages {string.Join(',', markedPages)}";

            if (markedPages.Count > 0)
            {
                var both = pass2.Document.Clone();
                MoveMark(both, pass2, markedPages[0], keepInFooter: true);
                var xc2Checks = Verify(block, marksRun, PdfReading.From(both, fixture, "expected", "accounting"), layout1);
                var failing = xc2Checks.Where(c => !c.Pass).ToList();
                xc2.Observed = [.. failing.Select(c => $"{c.Name} p{c.FirstFailingPage}")];
                xc2.Behaved = failing.Count == 1 && failing[0].Name == AccountingVerifier.Marks && failing[0].FirstFailingPage == markedPages[0] ? "yes" : "no";
                xc2.Detail = $"page {markedPages[0]}";
            }
        }
        else
        {
            xc1.Detail = xc2.Detail = $"base run {marksBase} has no pass-1 and pass-2 PDF";
        }

        // RB-3-swap: recorded, not counted.
        var swap = new ControlResult
        {
            Id = "RB-3-swap",
            Base = pageBoxBase,
            Change = "the labels of rows 10 and 11 exchanged in the reported read-back (the first definition of the order fault)",
            Expected = "recorded only: shows which checks a label swap fails",
            Counted = false,
        };
        results.Add(swap);
        if (runs.TryGetValue(pageBoxBase, out var swapRun) && swapRun.Pass1.Pdf is not null && Files.ReadReadBack(runsDirectory, swapRun.Pass2.Readback) is { } readBack)
        {
            var fixture = FixtureFile.ReadVerified(fixturesDirectory, swapRun.FixtureId, sums);
            var pass1 = await PdfReading.ReadAsync(Path.Combine(runsDirectory, swapRun.Pass1.Pdf), fixture, "none", "none", timeout, cancellation);
            foreach (var row in readBack.Rows)
            {
                row.Seq = row.Seq switch
                {
                    10 => 11,
                    11 => 10,
                    _ => row.Seq,
                };
            }

            var judged = ReadBackJudge.Judge(readBack, pass1, fixture);
            swap.Observed = [.. judged.Failures.Select(f => $"{f.Check} {f.Class}: {f.Detail}")];
            swap.Behaved = "recorded";
            swap.Detail = $"{judged.Failures.Select(f => f.Check).Distinct().Count()} check(s) fail: {string.Join(", ", judged.Failures.Select(f => f.Check).Distinct())}";
        }

        foreach (var r in results)
        {
            Console.WriteLine($"{r.Id}: {r.Behaved} — {r.Change}; expected {r.Expected}" + (r.Observed.Count > 0 ? $"; observed {string.Join(" | ", r.Observed.Take(6))}" : string.Empty) + (r.Detail is null ? string.Empty : $" ({r.Detail})"));
        }

        await Files.WriteJsonAsync(Path.Combine(outDirectory, "accounting-controls.json"), new { controls = results });
        return 0;
    }

    private static List<CheckResult> Verify(Block block, TwoPassLine run, PdfReading reading, LayoutResult layout1)
    {
        var layout = reading.ToLayout();
        var result = new RunResult
        {
            Id = run.Run,
            Configuration = $"{run.Markup}-{run.Strategy}",
            Block = block.Name,
            Footer = true,
            Repetition = run.Repetition,
            Purpose = "control",
            Status = "ok",
            Dialogs = run.Pass1.Dialogs ?? -1,
            AbortedRequests = run.Pass1.AbortedRequests ?? -1,
            ScriptsExpected = 0,
            ScriptsFound = run.Pass1.Scripts,
            Layout = layout,
        };
        var checks = AccountingVerifier.VerifyPaged(block, result);
        checks.Add(AccountingVerifier.CompareLayouts(AccountingVerifier.FooterNoDisplacement, layout1, layout));
        return [.. checks.Where(c => c.Name is not (AccountingVerifier.ColumnAlignment or AccountingVerifier.DescriptionText or AccountingVerifier.SplitRows))];
    }

    /// <summary>
    /// Prints "(continues)" in the amount band of the last line, on <paramref name="page"/>,
    /// of the row that continues there (by the original reading); removes the footer mark
    /// unless <paramref name="keepInFooter"/>.
    /// </summary>
    private static void MoveMark(PrintedDocument document, PdfReading original, int page, bool keepInFooter)
    {
        var seq = original.RowPages.Where(kv => kv.Value.Contains(page) && kv.Value.Max > page).Select(kv => kv.Key).FirstOrDefault();
        var lineIndex = Enumerable.Range(0, original.Document.ContentLines.Count).LastOrDefault(i => original.RowOf[i] == seq && original.Document.ContentLines[i].Page == page, -1);
        if (seq == 0 || lineIndex < 0)
        {
            throw new InvalidOperationException($"no continuing row on page {page}");
        }

        var line = document.ContentLines[lineIndex];
        var anchor = line.Words[0];
        var x0 = DeclaredGeometry.MmToPt(185);
        var mark = new PdfWord(page, x0, anchor.YMin, x0 + 40, anchor.YMax, PdfReading.Continues);
        line.Words.Add(mark);
        line.Words.Sort((a, b) => a.XMin.CompareTo(b.XMin));
        if (!keepInFooter)
        {
            document.FooterWords[page - 1].RemoveAll(w => w.Text == PdfReading.Continues);
        }
    }

    [GeneratedRegex(@"/MediaBox\s*\[([^\]]*)\]")]
    private static partial Regex MediaBoxArray();

    [GeneratedRegex(@"594\.9\d*")]
    private static partial Regex Width();
}
