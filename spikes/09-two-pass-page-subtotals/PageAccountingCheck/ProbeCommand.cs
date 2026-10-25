using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PaginationFixtures;

namespace PageAccountingCheck;

/// <summary>
/// The "probe" command: judges the two probes the harness ran before any matrix.
/// Marker probe (B100 and T300u, both markups, strategy "cell", pass 1):
///   (a) every annotation is a /Link with a /URI action whose address is a row marker; every
///       item row has exactly one "s", at least one "e" and exactly one "a"; nothing else;
///   (b) the reused text-integrity check passes the pass-1 PDFs (no marker text leaked);
///   (c) pages, per-page word counts and multi-page row spans equal the reference run's
///       (markers do not move the layout);
///   (d1) every end and amount rectangle lies on the page of the word it wraps (required: a failure stops);
///   (d2) each start and end rectangle holds the centre of the word's box within 1 pt (reported per
///   markup: where it fails, the amount cannot be stamped from the rectangle). The split-row probe applies
///   the same checks to every shared fixture with a split row and records, per split row, the fields
///   that characterise its end and amount rectangles.
/// Stamp probe (B100 table, both strategies, both passes):
///   (a) the second pass opened, read, stamped and saved; no marker is left; the page keys it changes;
///   (b) the page box of every pass-2 page, the content-stream count and the q … Q wrap;
///   (c) every stamp word is extracted exactly;
///   (d) strategy "stamped": every stamped amount on its row's last line (centre within ±1 pt of the
///       last word's centre, same line by the ±2 pt rule), and the mean difference;
///   (e) UO-1 and UO-2 against the matching reference.
/// Every finding is written to probe-&lt;kind&gt;.json with the raw qpdf annotation listing; the exit
/// code is 0 only when every item holds.
/// </summary>
internal static partial class ProbeCommand
{

    public static async Task<int> RunAsync(string kind, IReadOnlyDictionary<string, string> o, CancellationToken cancellation)
    {
        var timeout = TimeSpan.FromSeconds(300);
        var runsDirectory = o["runs"];
        var work = o.GetValueOrDefault("work") ?? Path.Combine(Path.GetTempPath(), "page-accounting-probe");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(o["out"]);
        var sums = Sums.Read(Path.Combine(o["fixtures"], FixtureFile.SumsFileName));
        var runs = Files.ReadTwoPass(runsDirectory);
        var integrity = Files.ReadIntegrity(o["integrity"]);
        var problems = new List<string>();
        var report = new JsonObject { ["probe"] = kind, ["qpdf"] = await Qpdf.VersionAsync(cancellation), ["runs"] = new JsonArray() };
        var reportRuns = report["runs"]!.AsArray();
        var gates = new List<(string Markup, bool D1, bool D2)>();
        var findings = new List<string>();
        var splitRows = new JsonArray();

        foreach (var run in runs.Values.OrderBy(r => r.Run, StringComparer.Ordinal))
        {
            var fixture = FixtureFile.ReadVerified(o["fixtures"], run.FixtureId, sums);
            var item = new JsonObject { ["run"] = run.Run, ["pass1"] = run.Pass1.Outcome, ["pass2"] = run.Pass2.Outcome, ["pass2Reason"] = run.Pass2.Reason };
            reportRuns.Add(item);
            if (run.Pass1.Outcome != "ok" || run.Pass1.Pdf is null)
            {
                problems.Add($"{run.Run}: pass 1 {run.Pass1.Outcome} ({run.Pass1.Reason})");
                continue;
            }

            var pass1Pdf = Path.Combine(runsDirectory, run.Pass1.Pdf);
            var pass1 = await PdfReading.ReadAsync(pass1Pdf, fixture, "none", "none", timeout, cancellation);
            var pages1 = await Qpdf.PagesAsync(pass1Pdf, work, timeout, cancellation);
            if (kind is "markers" or "split-rows" or "stamped-ab")
            {
                gates.Add(Markers(run, fixture, pass1, pages1, integrity, o.GetValueOrDefault("reference-integrity"), item, problems, splitRows, findings, kind));
            }
            else
            {
                await StampAsync(run, fixture, runsDirectory, pass1Pdf, pass1, pages1, integrity, work, item, problems, timeout, cancellation);
            }
        }

        if (runs.Count == 0)
        {
            problems.Add("no probe run recorded");
        }

        if (kind is "markers" or "split-rows" or "stamped-ab")
        {
            // (d1) and (d2) per markup; the coverage of the split-row probe.
            var markups = new JsonObject();
            foreach (var markup in gates.Select(g => g.Markup).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var set = gates.Where(g => g.Markup == markup).ToList();
                var rows = splitRows.Select(r => r!.AsObject()).Where(r => r["markup"]!.GetValue<string>() == markup).ToList();
                markups[markup] = new JsonObject
                {
                    ["runs"] = set.Count,
                    ["d1"] = set.All(g => g.D1) ? "pass" : "fail",
                    ["d2"] = set.All(g => g.D2) ? "pass" : "fail",
                    ["splitRows"] = rows.Count,
                    ["endWordOnFirstLine"] = rows.Count(r => r["e"]?["linesAbove"]?.GetValue<int>() == 0),
                    ["endWordOnDeepLine"] = rows.Count(r => r["e"]?["linesAbove"]?.GetValue<int>() >= 3),
                    ["classesOfE"] = new JsonObject([.. rows.GroupBy(r => r["e"]?["class"]?.GetValue<string>() ?? "none").Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))]),
                    ["classesOfA"] = new JsonObject([.. rows.GroupBy(r => r["a"]?["class"]?.GetValue<string>() ?? "none").Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))]),
                };
            }

            report["markups"] = markups;
            if (kind is "split-rows" or "stamped-ab")
            {
                report["splitRows"] = splitRows;
            }

            report["findings"] = new JsonArray([.. findings.Select(f => (JsonNode?)f)]);
        }

        report["problems"] = new JsonArray([.. problems.Select(p => (JsonNode?)p)]);
        report["result"] = problems.Count == 0 ? "pass" : "stop";
        await File.WriteAllTextAsync(Path.Combine(o["out"], $"probe-{kind}.json"), report.ToJsonString(Files.Json) + "\n");
        foreach (var problem in problems)
        {
            Console.WriteLine($"PROBLEM: {problem}");
        }

        Console.WriteLine($"Probe {kind}: {(problems.Count == 0 ? "pass" : "stop")} ({runs.Count} runs).");
        return problems.Count == 0 ? 0 : 1;
    }

    private static (string Markup, bool D1, bool D2) Markers(
        TwoPassLine run, Fixture fixture, PdfReading pass1, List<QpdfPage> pages, Dictionary<string, IntegrityEntry> integrity, string? referencePath, JsonObject item, List<string> problems, JsonArray splitRows,
        List<string> findings, string kind)
    {
        var stamped = run.Strategy == "stamped";
        var amountMarkers = stamped ? 0 : 1;
        // (a) the annotations, as qpdf reads them.
        var dump = new JsonArray();
        var unknown = 0;
        var markers = new Dictionary<int, List<(char Kind, int Page, double[] Rect)>>();
        foreach (var page in pages)
        {
            var height = page.MediaBox?.Numbers[3] is { } top ? (double)top : 841.91998;
            foreach (var (subtype, action, uri, rect) in page.Annotations)
            {
                dump.Add($"p{page.Page} {subtype} {action} {uri} [{rect}]");
                var match = uri is null ? Match.Empty : MarkerPattern().Match(uri);
                var numbers = rect?.Split(' ').Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                if (subtype != "/Link" || action != "/URI" || !match.Success || numbers is not { Length: 4 })
                {
                    unknown++;
                    continue;
                }

                var seq = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var topLeft = new[] { Math.Min(numbers[0], numbers[2]), height - Math.Max(numbers[1], numbers[3]), Math.Max(numbers[0], numbers[2]), height - Math.Min(numbers[1], numbers[3]) };
                if (!markers.TryGetValue(seq, out var list))
                {
                    list = [];
                    markers[seq] = list;
                }

                list.Add((match.Groups[2].Value[0], page.Page, topLeft));
            }
        }

        var expected = fixture.Items.Select(r => r.Sequence!.Value).ToList();
        var badRows = expected.Where(s => !markers.TryGetValue(s, out var l) || l.Count(m => m.Kind == 's') != 1 || l.Count(m => m.Kind == 'e') < 1 || l.Count(m => m.Kind == 'a') != amountMarkers).ToList();
        var extra = markers.Keys.Where(s => !expected.Contains(s)).ToList();
        var aPass = unknown == 0 && badRows.Count == 0 && extra.Count == 0;
        item["a"] = new JsonObject
        {
            ["pass"] = aPass,
            ["annotations"] = dump.Count,
            ["unknown"] = unknown,
            ["rowsWithWrongMarkerCounts"] = new JsonArray([.. badRows.Take(20).Select(x => (JsonNode?)x)]),
            ["sequencesNotInBlock"] = new JsonArray([.. extra.Take(20).Select(x => (JsonNode?)x)]),
            ["rowsWithSeveralEndRectangles"] = markers.Count(kv => kv.Value.Count(m => m.Kind == 'e') > 1),
            ["qpdfListing"] = dump,
        };
        // The one recorded (a) result of option A: in grid markup on the two bottom-aligned-amount fixtures, a
        // split row whose only fault is a missing end marker (one start, the expected amount markers, no end).
        var missingEndOnly = unknown == 0 && extra.Count == 0 && badRows.Count > 0
            && badRows.All(s => markers.TryGetValue(s, out var l) && l.Count(m => m.Kind == 's') == 1 && !l.Any(m => m.Kind == 'e') && l.Count(m => m.Kind == 'a') == amountMarkers && pass1.RowPages.TryGetValue(s, out var rp) && rp.Count > 1);
        var recorded = !aPass && missingEndOnly && run.Markup == "grid" && run.FixtureId is "T300u-ab" or "X64K-ab";
        item["a"]!["finding"] = recorded ? $"end marker missing for split row(s) {string.Join(',', badRows)} — recorded result, not a stop" : null;
        if (recorded)
        {
            findings.Add($"{run.Run} (a): end marker missing for split row(s) {string.Join(',', badRows)} (one start, {amountMarkers} amount, no end) — recorded result");
        }
        else if (!aPass)
        {
            problems.Add($"{run.Run} (a): {unknown} other annotation(s), {badRows.Count} row(s) with wrong marker counts, {extra.Count} unknown sequence(s)");
        }
        else if (unknown > 0)
        {
            problems.Add($"{run.Run} (a): {unknown} other annotation(s)");
        }

        // (b) the reused text-integrity check on the pass-1 PDF.
        var ti = integrity.GetValueOrDefault(run.Run + ".p1");
        if (stamped)
        {
            // A first-pass PDF in strategy "stamped" has no amounts: the text check fails it by construction.
            item["b"] = new JsonObject { ["verdict"] = "n/a (strategy stamped: no amounts in the first pass)" };
        }
        else
        {
            item["b"] = new JsonObject { ["verdict"] = ti?.Verdict, ["failures"] = new JsonArray([.. (ti?.Failures ?? []).Take(10).Select(f => (JsonNode?)$"{f.Rule} {f.Class} row {f.Row} p{f.Page} {f.Detail}")]) };
            if (ti?.Verdict != "pass")
            {
                problems.Add($"{run.Run} (b): text integrity {ti?.Verdict ?? "missing"}");
            }
        }

        // (c) markers do not move the layout.
        var reference = referencePath is null ? null : Files.ReadIntegrity(referencePath).GetValueOrDefault($"{run.FixtureId}-{run.Markup}-r1");
        var spans = stamped
            ? string.Join(",", pass1.RowPages.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value.Min}-{kv.Value.Max}"))
            : string.Join(",", (ti?.Observations?.MultiPageRows ?? []).OrderBy(s => s.Row).Select(s => $"{s.Row}:{s.FirstPage}-{s.LastPage}"));
        var referenceSpans = string.Join(",", (reference?.Observations?.MultiPageRows ?? []).OrderBy(s => s.Row).Select(s => $"{s.Row}:{s.FirstPage}-{s.LastPage}"));
        var cPass = stamped
            ? reference is not null && pass1.Pages == reference.Pages && spans == referenceSpans
            : reference is not null && ti?.Pages == reference.Pages && spans == referenceSpans
                && ti?.PageWordCounts is not null && reference.PageWordCounts is not null && ti.PageWordCounts.SequenceEqual(reference.PageWordCounts);
        item["c"] = new JsonObject
        {
            ["pass"] = cPass,
            ["reference"] = reference is null ? null : $"{run.FixtureId}-{run.Markup}-r1",
            ["pages"] = $"{(stamped ? pass1.Pages : ti?.Pages)} against {reference?.Pages}",
            ["note"] = stamped ? "strategy stamped: pages and spans only (the amount cell is empty, so the layout may differ); recorded, not a stop" : null,
            ["pageWordCounts"] = $"[{string.Join(',', ti?.PageWordCounts ?? [])}] against [{string.Join(',', reference?.PageWordCounts ?? [])}]",
            ["spans"] = $"{spans} against {referenceSpans}",
        };
        if (!cPass && !stamped)
        {
            problems.Add($"{run.Run} (c): the layout differs from the reference run (stop and ask)");
        }

        // (d2) position — the first marker probe's (d), unchanged: the rectangles hold the words they wrap.
        var misplaced = new List<string>();
        foreach (var (seq, list) in markers.Where(kv => expected.Contains(kv.Key)))
        {
            var start = list.FirstOrDefault(m => m.Kind == 's');
            if (start.Rect is not null && pass1.SeqWord.TryGetValue(seq, out var seqWord) && !Holds(start.Rect, seqWord.XCentre, seqWord.YCentre))
            {
                misplaced.Add($"row {seq} s");
            }

            var ends = list.Where(m => m.Kind == 'e').ToList();
            if (ends.Count > 0 && pass1.EndWord(seq) is { } endWord)
            {
                var lastPage = ends.Max(m => m.Page);
                var end = ends.Where(m => m.Page == lastPage).OrderBy(m => m.Rect[3]).Last();
                if (!Holds(end.Rect, endWord.XCentre, endWord.YCentre))
                {
                    misplaced.Add($"row {seq} e");
                }
            }
        }

        item["d2"] = new JsonObject { ["pass"] = misplaced.Count == 0, ["misplaced"] = new JsonArray([.. misplaced.Take(20).Select(x => (JsonNode?)x)]) };

        // (d1) page: every e and a rectangle lies on the page where its word prints.
        var offPage = new List<string>();
        foreach (var (seq, list) in markers.Where(kv => expected.Contains(kv.Key)))
        {
            if (EndRect(list) is { } end && pass1.EndWord(seq) is { } endWord && end.Page != endWord.Page)
            {
                offPage.Add($"row {seq} e: rectangle p{end.Page}, word p{endWord.Page}");
            }

            var amountWords = pass1.AmountWords(seq);
            foreach (var a in list.Where(m => m.Kind == 'a'))
            {
                if (amountWords.Count > 0 && a.Page != amountWords[^1].Word.Page)
                {
                    offPage.Add($"row {seq} a: rectangle p{a.Page}, word p{amountWords[^1].Word.Page}");
                }
            }
        }

        item["d1"] = new JsonObject { ["pass"] = offPage.Count == 0, ["offPage"] = new JsonArray([.. offPage.Take(20).Select(x => (JsonNode?)x)]) };
        if (offPage.Count > 0 && kind != "stamped-ab")
        {
            problems.Add($"{run.Run} (d1): {offPage.Count} rectangle(s) not on their word's page (stop, routed to the architect-reviewer or the human)");
        }

        // The split-row fields (raw evidence; never used to correct anything).
        foreach (var (seq, rowPages) in pass1.RowPages.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key))
        {
            if (!markers.TryGetValue(seq, out var list))
            {
                continue;
            }

            var amountWords = pass1.AmountWords(seq);
            var amountRect = list.FirstOrDefault(m => m.Kind == 'a');
            splitRows.Add(new JsonObject
            {
                ["run"] = run.Run,
                ["fixture"] = run.FixtureId,
                ["markup"] = run.Markup,
                ["row"] = seq,
                ["rowPages"] = $"{rowPages.Min}-{rowPages.Max}",
                ["e"] = EndRect(list) is { } end && pass1.EndWord(seq) is { } endWord ? Fields(pass1, seq, end.Page, end.Rect, endWord) : null,
                ["a"] = amountRect.Rect is not null && amountWords.Count > 0 ? Fields(pass1, seq, amountRect.Page, amountRect.Rect, amountWords[^1].Word) : null,
            });
        }

        item["harnessReading"] = $"{run.Pass2.Outcome}{(run.Pass2.Reason is null ? string.Empty : $" ({run.Pass2.Reason})")}";
        return (run.Markup, offPage.Count == 0, misplaced.Count == 0);
    }

    /// <summary>The end rectangle the second pass uses: the lowest "e" rectangle on the last page that holds one.</summary>
    private static (int Page, double[] Rect)? EndRect(List<(char Kind, int Page, double[] Rect)> list)
    {
        var ends = list.Where(m => m.Kind == 'e').ToList();
        if (ends.Count == 0)
        {
            return null;
        }

        var lastPage = ends.Max(m => m.Page);
        var end = ends.Where(m => m.Page == lastPage).OrderBy(m => m.Rect[3]).Last();
        return (end.Page, end.Rect);
    }

    /// <summary>
    /// One rectangle against the word it wraps: pages, signed offsets (rectangle centre minus
    /// word-box centre, top-left y), the row's lines above the word's line on that page, the
    /// intra-fragment distance (word line top minus the row's first line top on that page),
    /// the repeated header's height on that page, the fragment index, and the class.
    /// </summary>
    private static JsonObject Fields(PdfReading reading, int seq, int rectPage, double[] rect, TextIntegrityCheck.PdfWord word)
    {
        var lines = reading.Document.ContentLines;
        double Top(int i) => lines[i].Words.Min(w => w.YMin);
        var wordLine = Enumerable.Range(0, lines.Count).First(i => lines[i].Words.Any(w => ReferenceEquals(w, word)));
        var rowLines = Enumerable.Range(0, lines.Count).Where(i => reading.RowOf[i] == seq && lines[i].Page == word.Page).ToList();
        var linesAbove = rowLines.Count(i => i < wordLine);
        var intra = Top(wordLine) - Top(rowLines.Min());
        var pageLines = Enumerable.Range(0, lines.Count).Where(i => lines[i].Page == word.Page).ToList();
        var header = pageLines.FirstOrDefault(i => reading.Rules.IsHeaderRow(lines[i]), -1);
        var afterHeader = header < 0 ? -1 : pageLines.FirstOrDefault(i => i > header && !reading.Rules.IsHeaderRow(lines[i]), -1);
        var headerHeight = header >= 0 && afterHeader >= 0 ? Top(afterHeader) - Top(header) : 0.0;
        var offsetY = ((rect[1] + rect[3]) / 2) - word.YCentre;
        var offsetX = ((rect[0] + rect[2]) / 2) - word.XCentre;
        var rowPages = reading.RowPages[seq].ToList();
        var kind = rectPage != word.Page ? "other"
            : Math.Abs(offsetY) <= ReadBackJudge.PositionTolerancePt ? "within-tolerance"
            : Math.Abs(offsetY - intra) <= ReadBackJudge.PositionTolerancePt ? "matches-intra-fragment"
            : Math.Abs(offsetY - headerHeight) <= ReadBackJudge.PositionTolerancePt ? "matches-header"
            : "other";
        return new JsonObject
        {
            ["word"] = word.Text,
            ["rectanglePage"] = rectPage,
            ["wordPage"] = word.Page,
            ["offsetYPt"] = Math.Round(offsetY, 3),
            ["offsetXPt"] = Math.Round(offsetX, 3),
            ["linesAbove"] = linesAbove,
            ["intraFragmentDistancePt"] = Math.Round(intra, 3),
            ["headerHeightPt"] = Math.Round(headerHeight, 3),
            ["fragmentIndex"] = rowPages.IndexOf(word.Page),
            ["fragments"] = rowPages.Count,
            ["class"] = kind,
        };
    }

    private static async Task StampAsync(
        TwoPassLine run, Fixture fixture, string runsDirectory, string pass1Pdf, PdfReading pass1, List<QpdfPage> pages1, Dictionary<string, IntegrityEntry> integrity,
        string work, JsonObject item, List<string> problems, TimeSpan timeout, CancellationToken cancellation)
    {
        // (a) the second pass ran and left no marker; the page keys it changed.
        if (run.Pass2.Outcome != "ok" || run.Pass2.Pdf is null)
        {
            problems.Add($"{run.Run} (a): pass 2 {run.Pass2.Outcome} ({run.Pass2.Reason})");
            return;
        }

        var pass2Pdf = Path.Combine(runsDirectory, run.Pass2.Pdf);
        var pages2 = await Qpdf.PagesAsync(pass2Pdf, work, timeout, cancellation);
        var markersLeft = pages2.Sum(p => p.Annotations.Count(a => a.Uri is not null && MarkerPattern().IsMatch(a.Uri)));

        // The exact per-page key difference (first pass → stamped), and the transparency and colour entries of both PDFs.
        var keyDifferences = new JsonArray();
        var violations = 0;
        foreach (var (before, after) in pages1.Zip(pages2))
        {
            var found = StampingSideEffects.KeyDifference(before, after);
            violations += found.Count;
            keyDifferences.Add(new JsonObject
            {
                ["page"] = before.Page,
                ["exact"] = found.Count == 0,
                ["group"] = after.GroupValue,
                ["violations"] = new JsonArray([.. found.Select(x => (JsonNode?)x)]),
            });
        }

        var counts1 = await StampingSideEffects.CountAsync(Path.Combine(runsDirectory, run.Pass1.Pdf!), work, timeout, cancellation);
        var counts2 = await StampingSideEffects.CountAsync(pass2Pdf, work, timeout, cancellation);
        JsonObject Counts(TransparencyCounts c) => new()
        {
            ["softMasks"] = c.SoftMasks,
            ["blendModesNotNormal"] = c.BlendModesNotNormal,
            ["alphaBelowOne"] = c.AlphaBelowOne,
            ["colourSpaces"] = c.ColourSpaces,
            ["patterns"] = c.Patterns,
        };
        item["a"] = new JsonObject
        {
            ["markersLeft"] = markersLeft,
            ["keyDifferenceExact"] = violations == 0,
            ["pages"] = keyDifferences,
            ["page1KeysBefore"] = new JsonObject([.. pages1[0].Keys.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))]),
            ["page1KeysAfter"] = new JsonObject([.. pages2[0].Keys.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))]),
            ["pass1Counts"] = Counts(counts1),
            ["pass2Counts"] = Counts(counts2),
        };
        if (markersLeft > 0 || pages1.Count != pages2.Count)
        {
            problems.Add($"{run.Run} (a): {markersLeft} marker(s) left, {pages1.Count} → {pages2.Count} pages");
        }

        if (violations > 0)
        {
            problems.Add($"{run.Run} (a): the page key difference is not exact ({violations} violation(s)) (stop and ask)");
        }

        if (counts1.Any)
        {
            problems.Add($"{run.Run} (a): pass-1 precondition counts not all 0 — {counts1} (stop and ask)");
        }

        if (counts2.AnyTransparency)
        {
            problems.Add($"{run.Run} (a): pass-2 transparency counts not all 0 — {counts2} (stop and ask)");
        }

        // (b) page boxes, content streams and the q … Q wrap.
        var boxes = pages2.Select(p => p.MediaBox is null ? Qpdf.Differs : Qpdf.Classify(p.MediaBox.Numbers)).ToList();
        var first = pages2[0].Contents;
        var wrapFirst = first.Count > 0 ? await StreamTextAsync(pass2Pdf, first[0], timeout, cancellation) : null;
        var wrapLast = first.Count > 2 ? await StreamTextAsync(pass2Pdf, first[^2], timeout, cancellation) : null;
        item["b"] = new JsonObject
        {
            ["pass1Boxes"] = string.Join(", ", pages1.Select(p => p.MediaBox?.Text).Distinct()),
            ["pass2Boxes"] = string.Join(", ", pages2.Select(p => p.MediaBox?.Text).Distinct()),
            ["pass2BoxClasses"] = string.Join(", ", boxes.Distinct()),
            ["contentsBefore"] = pages1[0].Contents.Count,
            ["contentsAfter"] = first.Count,
            ["firstStream"] = wrapFirst?.Trim(),
            ["streamBeforeTheStamp"] = wrapLast?.Trim(),
        };
        if (pages1.Any(p => p.MediaBox?.Text != Qpdf.SourceBoxText))
        {
            problems.Add($"{run.Run} (b): a pass-1 box is not [{Qpdf.SourceBoxText}] (stop and ask)");
        }

        if (boxes.Contains(Qpdf.Differs))
        {
            problems.Add($"{run.Run} (b): a pass-2 page box is neither the source box nor the rounded box");
        }

        // (c) every stamp word extracted exactly.
        var pass2 = await PdfReading.ReadAsync(pass2Pdf, fixture, "expected", "accounting", timeout, cancellation);
        var unreadable = new List<string>();
        for (var page = 1; page <= pass2.Pages; page++)
        {
            var (left, centre, right) = pass2.FooterThirds(page);
            if (!Parses(left, "Brought forward: ") || !Parses(centre, "Page subtotal: ") || !Parses(right, "Carried forward: "))
            {
                unreadable.Add($"p{page}: '{left}' | '{centre}' | '{right}'");
            }
        }

        var stamped = run.Strategy == "stamped";
        var amountsWrong = stamped
            ? fixture.Items.Where(r => string.Join(' ', pass2.AmountWords(r.Sequence!.Value).Select(w => w.Word.Text)) != r.Amount).Select(r => r.Sequence!.Value).ToList()
            : new List<int>();
        var marks = Enumerable.Range(1, pass2.Pages).Sum(p => pass2.MarksOn(p).Footer);
        item["c"] = new JsonObject
        {
            ["unreadableFooters"] = new JsonArray([.. unreadable.Take(10).Select(x => (JsonNode?)x)]),
            ["marksExtracted"] = marks,
            ["amountsNotExtractedExactly"] = new JsonArray([.. amountsWrong.Take(20).Select(x => (JsonNode?)x)]),
            ["textIntegrityPass2"] = integrity.GetValueOrDefault(run.Run + ".p2")?.Verdict,
        };
        if (unreadable.Count > 0 || amountsWrong.Count > 0)
        {
            problems.Add($"{run.Run} (c): {unreadable.Count} unreadable footer(s), {amountsWrong.Count} amount(s) not extracted exactly");
        }

        // (d) stamped amounts on the row's last line.
        if (stamped)
        {
            var differences = new List<double>();
            var off = new List<string>();
            foreach (var row in fixture.Items)
            {
                var seq = row.Sequence!.Value;
                var amount = pass2.AmountWords(seq);
                var end = pass2.EndWord(seq);
                if (amount.Count == 0 || end is null)
                {
                    off.Add($"row {seq}: no amount or no last word");
                    continue;
                }

                var difference = amount[^1].Word.YCentre - end.YCentre;
                differences.Add(difference);
                var endLine = Enumerable.Range(0, pass2.Document.ContentLines.Count).First(i => pass2.Document.ContentLines[i].Words.Contains(end));
                if (Math.Abs(difference) > 1.0 || amount[^1].Line != endLine)
                {
                    off.Add(string.Create(CultureInfo.InvariantCulture, $"row {seq}: centre difference {difference:0.###} pt, same line {amount[^1].Line == endLine}"));
                }
            }

            var mean = differences.Count == 0 ? 0 : differences.Average();
            item["d"] = new JsonObject
            {
                ["rows"] = differences.Count,
                ["meanDifferencePt"] = Math.Round(mean, 4),
                ["minDifferencePt"] = differences.Count == 0 ? null : Math.Round(differences.Min(), 4),
                ["maxDifferencePt"] = differences.Count == 0 ? null : Math.Round(differences.Max(), 4),
                ["off"] = new JsonArray([.. off.Take(20).Select(x => (JsonNode?)x)]),
            };
            if (off.Count > 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{run.Run} (d): {off.Count} amount(s) not on the last line; mean difference {mean:0.###} pt"));
            }
        }

        // (e) pixels against the matching reference.
        var unchanged = await UnchangedOutside.CheckAsync(pass1Pdf, pass2Pdf, pass1, pass2, stamped, work, null, timeout, cancellation, colour: true);
        var pixelFailures = unchanged.Failures.Where(f => f.Check is "UO-1" or "UO-2" or "UO-4").ToList();
        item["e"] = new JsonObject
        {
            ["boxVerdict"] = unchanged.BoxVerdict,
            ["refRounded"] = unchanged.Rounded is null ? null : $"{(unchanged.Rounded.Pass ? "pass" : "fail")}: {unchanged.Rounded.Failure ?? $"{unchanged.Rounded.DifferingBytes} bytes, sha256 {unchanged.Rounded.CopySha256}"}",
            ["pages"] = new JsonArray([.. unchanged.Pages.Select(p => (JsonNode?)$"p{p.Page} against {p.Reference}: {p.Changed} changed, {p.Outside} outside, {p.OverInk} over ink")]),
            ["failures"] = new JsonArray([.. unchanged.Failures.Take(20).Select(f => (JsonNode?)$"{f.Check} {f.Class} p{f.Page}: {f.Detail}")]),
        };
        if (pixelFailures.Count > 0)
        {
            problems.Add($"{run.Run} (e): {string.Join("; ", pixelFailures.Take(5).Select(f => $"{f.Check} {f.Class} p{f.Page}"))}");
        }

        // (f) the colour check.
        item["f"] = new JsonObject
        {
            ["pages"] = new JsonArray([.. unchanged.ColourPages.Select(p => (JsonNode?)$"p{p.Page} against {p.Reference}: {p.Changed} changed, {p.Outside} outside, {p.OverInk} over ink")]),
            ["failures"] = new JsonArray([.. unchanged.ColourFailures.Select(f => (JsonNode?)$"{f.Class} p{f.Page}: {f.Detail}")]),
        };
        if (unchanged.ColourFailures.Count > 0 || unchanged.ColourPages.Count != pages2.Count)
        {
            problems.Add($"{run.Run} (f): colour check — {unchanged.ColourFailures.Count} failure(s), {unchanged.ColourPages.Count} of {pages2.Count} pages compared (stop and ask)");
        }

        // (g) qpdf --check of the pass-2 PDF.
        var structure = await StampingSideEffects.CheckAsync(pass2Pdf, timeout, cancellation);
        var structure1 = await StampingSideEffects.CheckAsync(pass1Pdf, timeout, cancellation);
        item["g"] = new JsonObject
        {
            ["pass2"] = $"{structure.Verdict} (exit {structure.ExitCode}, {structure.WarningLines} warning lines){(structure.Reason is null ? string.Empty : ": " + structure.Reason)}",
            ["pass1"] = $"{structure1.Verdict} (exit {structure1.ExitCode}, {structure1.WarningLines} warning lines){(structure1.Reason is null ? string.Empty : ": " + structure1.Reason)}",
        };
        if (structure.Verdict == StampingSideEffects.Fail)
        {
            problems.Add($"{run.Run} (g): qpdf --check classifies the pass-2 PDF fail ({structure.Reason}) (stop and ask)");
        }
    }

    private static async Task<string?> StreamTextAsync(string pdf, string reference, TimeSpan timeout, CancellationToken cancellation)
    {
        var parts = reference.Split(' ');
        var result = await Tool.RunAsync("qpdf", [$"--show-object={parts[0]},{parts[1]}", "--filtered-stream-data", pdf], timeout, cancellation);
        return result.ExitCode is 0 or 3 ? result.StandardOutput : null;
    }

    private static bool Holds(double[] rect, double x, double y) =>
        x >= rect[0] - ReadBackJudge.PositionTolerancePt && x <= rect[2] + ReadBackJudge.PositionTolerancePt
        && y >= rect[1] - ReadBackJudge.PositionTolerancePt && y <= rect[3] + ReadBackJudge.PositionTolerancePt;

    private static bool Parses(string? text, string prefix) =>
        text is not null && text.StartsWith(prefix, StringComparison.Ordinal)
        && decimal.TryParse(text[prefix.Length..], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);

    [GeneratedRegex(@"^https://m\.invalid/r/(\d{1,8})/(s|e|a)$")]
    private static partial Regex MarkerPattern();
}
