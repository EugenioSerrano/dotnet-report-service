using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RowEndSweep;

/// <summary>One link annotation of row 50 as the checker's qpdf listing shows it.</summary>
internal sealed record RowLink(char Kind, int Page, string Rect);

/// <summary>Everything the report knows about one run.</summary>
internal sealed class RunFacts
{
    public required string Round { get; init; }
    public required string Run { get; init; }
    public required string Fixture { get; init; }
    public required string Content { get; init; }
    public required int Kib { get; init; }
    public required string Markup { get; init; }
    public required int Repetition { get; init; }
    public int? RecordBytes { get; set; }
    public int? DescriptionCharacters { get; set; }
    public string? LastWord { get; set; }
    public string Pass1 { get; set; } = "missing";
    public string? Pass1Reason { get; set; }
    public double? WallMs { get; set; }
    public double? PdfMs { get; set; }
    public long? ChromiumPeakKiB { get; set; }
    public string? ChromiumVersion { get; set; }
    public int? Escapes { get; set; }
    public string Reading { get; set; } = "missing";
    public string? ReadingReason { get; set; }
    public int? ReadingEndMarkers { get; set; }
    public int? ReadingEndPage { get; set; }
    public string? TextVerdict { get; set; }
    public int? Pages { get; set; }
    public int? FirstPage { get; set; }
    public int? LastPage { get; set; }
    public int? Span => FirstPage is { } f && LastPage is { } l ? l - f + 1 : null;
    public int? Annotations { get; set; }
    public int? Unknown { get; set; }
    public List<int> WrongRows { get; set; } = [];
    public List<int> OtherRowFaults { get; set; } = [];
    public List<RowLink> Row50 { get; set; } = [];
    public List<string> Row50Lines { get; set; } = [];
    public JsonObject? EndFields { get; set; }
    public bool ArgvMatches { get; set; }
    public bool? EndPresent => Annotations is null ? null : Row50.Any(l => l.Kind == 'e');
    public int? EndPage => Row50.Where(l => l.Kind == 'e').Select(l => (int?)l.Page).Max();
    public string EndRects => string.Join(" ", Row50.Where(l => l.Kind == 'e').Select(l => $"p{l.Page}[{l.Rect}]"));
    public int? StartPage => Row50.Where(l => l.Kind == 's').Select(l => (int?)l.Page).FirstOrDefault();
    public int? AmountPage => Row50.Where(l => l.Kind == 'a').Select(l => (int?)l.Page).FirstOrDefault();
    public bool? EndOnRowLastPage => EndPresent == true && LastPage is not null ? EndPage == LastPage : null;
    public bool? ReadersAgreePresence => EndPresent is null || ReadingEndMarkers is null ? null : EndPresent == ReadingEndMarkers > 0;
    public bool? ReadersAgreePage => EndPresent == true && ReadingEndPage is not null ? EndPage == ReadingEndPage : null;
    public string? NotCounted { get; set; }

    /// <summary>The fields three repetitions must agree on.</summary>
    public string Signature => $"{Pages}|{FirstPage}-{LastPage}|{EndPresent}|{EndRects}|{StartPage}|{AmountPage}|{Annotations}";
}

/// <summary>
/// The report: joins, per run, the driver's records (plan, twopass.jsonl, argv.jsonl,
/// readback), the text checker's integrity.json (pages, the row's printed span from
/// poppler words) and the accounting checker's split-row probe (qpdf's annotation
/// listing and the end-rectangle fields), then applies the instrument checks, the
/// reproduction checks against the earlier experiment's own PDFs and probe, the
/// repeat agreement, the coverage rule, the classification per markup and content and
/// the span-alone verdict, all fixed before the runs. It reads files only.
/// </summary>
internal static partial class SweepReport
{
    private const int TallRow = 50;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(IReadOnlyDictionary<string, string> o)
    {
        var sizes = ReadSizes(Path.Combine(o["fixtures"], SweepFixtures.SizesFile));
        var runs = new List<RunFacts>();
        var instrument = new List<string>();
        var stops = new List<string>();
        var rounds = new JsonArray();
        foreach (var roundDirectory in o["rounds"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (facts, info) = ReadRound(roundDirectory, sizes, instrument);
            runs.AddRange(facts);
            rounds.Add(info);
        }

        var reference = ReadRound(o["reference"], sizes, instrument, isReference: true).Facts.ToDictionary(f => f.Run, StringComparer.Ordinal);
        var referenceSplitRows = JsonNode.Parse(File.ReadAllText(o["reference-split-rows"]))!["splitRows"]!.AsArray()
            .Select(n => n!.AsObject()).ToDictionary(n => n["run"]!.GetValue<string>(), StringComparer.Ordinal);

        // Instrument checks I-1 … I-5.
        foreach (var f in runs)
        {
            if (!f.ArgvMatches)
            {
                stops.Add($"{f.Run}: a child's command line differs from the earlier driver's");
            }

            if (f.Pass1 != "ok" || f.Annotations is null || f.TextVerdict is null || f.Reading == "missing")
            {
                f.NotCounted = $"I-1 pass 1 {f.Pass1}{(f.Annotations is null ? ", no annotation reading" : string.Empty)}{(f.TextVerdict is null ? ", no text check" : string.Empty)}";
                continue;
            }

            if (f.Unknown > 0 || f.OtherRowFaults.Count > 0)
            {
                stops.Add($"{f.Run}: I-2 {f.Unknown} unknown annotation(s), rows other than {TallRow} with wrong marker counts: {string.Join(',', f.OtherRowFaults)}");
                f.NotCounted = "I-2";
                continue;
            }

            if (f.Row50.Count(l => l.Kind == 's') != 1 || f.Row50.Count(l => l.Kind == 'a') != 1)
            {
                stops.Add($"{f.Run}: I-3 row {TallRow} start or amount link count is not one");
                f.NotCounted = "I-3";
                continue;
            }

            if (f.TextVerdict != "pass" || f.Span is null)
            {
                f.NotCounted = $"I-4 text check {f.TextVerdict}";
                continue;
            }

            if (f.ReadersAgreePresence == false)
            {
                stops.Add($"{f.Run}: I-5 the harness reading and qpdf disagree on the end link's presence");
                f.NotCounted = "I-5 presence";
            }
        }

        var counted = runs.Where(f => f.NotCounted is null && f.Round != "smoke").ToList();
        var measured = runs.Where(f => f.Round != "smoke").ToList();
        var pageOnly = measured.Where(f => f.ReadersAgreePage == false).Select(f => f.Run).ToList();
        var presenceDisagreement = measured.Count(f => f.ReadersAgreePresence == false);
        if (measured.Count > 0 && presenceDisagreement * 100.0 / measured.Count > 1.0)
        {
            stops.Add($"I-5 presence disagreements on {presenceDisagreement} of {measured.Count} runs (above 1 %)");
        }

        // Chromium build and harness: one value across every run of every round.
        var versions = runs.Where(f => f.ChromiumVersion is not null).Select(f => f.ChromiumVersion!).Distinct(StringComparer.Ordinal).ToList();
        if (versions.Count > 1)
        {
            stops.Add($"more than one Chromium version across the runs: {string.Join(", ", versions)}");
        }

        var caps = runs.Where(f => f.Pass1 is "timeout" or "killed-wall-clock" or "oom-killed" or "crashed" || f.Reading is "timeout" or "killed-wall-clock" or "oom-killed" or "crashed").Select(f => f.Run).ToList();
        if (caps.Count > 0)
        {
            stops.Add($"capped passes: {string.Join(", ", caps)}");
        }

        var escapes = runs.Where(f => f.Escapes is > 0).Select(f => f.Run).ToList();

        // Reproduction of the earlier observations.
        var reproduction = new JsonArray
        {
            Reproduce("R-1", "X64K", "table", runs, reference, referenceSplitRows, expectEnd: true),
            Reproduce("R-2", "X128K", "table", runs, reference, referenceSplitRows, expectEnd: false),
            Reproduce("R-3", "X64K", "grid", runs, reference, referenceSplitRows, expectEnd: true),
            Reproduce("new", "X128K", "grid", runs, reference, referenceSplitRows, expectEnd: null),
        };

        // Repeat agreement per fixture and markup.
        var groups = counted.GroupBy(f => (f.Fixture, f.Markup)).ToList();
        var notReproducible = groups.Where(g => g.Select(f => f.Signature).Distinct(StringComparer.Ordinal).Count() > 1).ToDictionary(g => g.Key, g => g.ToList());
        var incomplete = measured.GroupBy(f => (f.Fixture, f.Markup)).Where(g => g.Count(f => f.NotCounted is null) < SweepPlan.Repetitions)
            .Select(g => $"{g.Key.Fixture} {g.Key.Markup}: {g.Count(f => f.NotCounted is null)} counted of {g.Count()}").ToList();

        // Per markup and content: spans, coverage, class.
        var classes = new JsonObject();
        var proposal = new List<string>();
        var limits = new Dictionary<(string Markup, string Content), (string Class, int? Last, int? First, Dictionary<int, string> Presence)>();
        foreach (var markup in SweepPlan.Markups)
        {
            var perContent = new JsonObject();
            foreach (var content in SweepPlan.Contents)
            {
                var series = counted.Where(f => f.Markup == markup && f.Content == content).ToList();
                var bySize = series.GroupBy(f => f.Kib).OrderBy(g => g.Key)
                    .Select(g => (Kib: g.Key, Fixture: g.First().Fixture, Spans: g.Select(f => f.Span!.Value).Distinct().Order().ToList(), Present: g.Select(f => f.EndPresent!.Value).Distinct().ToList()))
                    .ToList();
                var presence = series.GroupBy(f => f.Span!.Value).OrderBy(g => g.Key).ToDictionary(
                    g => g.Key,
                    g => g.All(f => f.EndPresent == true) ? "present" : g.All(f => f.EndPresent == false) ? "absent" : "mixed");

                // Coverage on repeat-agreed sizes.
                var agreed = bySize.Where(s => s.Spans.Count == 1).ToList();
                var gaps = new List<string>();
                for (var i = 1; i < agreed.Count; i++)
                {
                    if (agreed[i].Spans[0] - agreed[i - 1].Spans[0] > 1)
                    {
                        gaps.Add($"{agreed[i - 1].Kib}→{agreed[i].Kib} KiB: {agreed[i - 1].Spans[0]}→{agreed[i].Spans[0]} pages");
                        if (agreed[i].Kib - agreed[i - 1].Kib >= 2)
                        {
                            proposal.Add(SweepPlan.Id(content, (agreed[i].Kib + agreed[i - 1].Kib) / 2));
                        }
                    }
                }

                var spansSeen = presence.Keys.ToHashSet();
                var missing = Enumerable.Range(13, 13).Where(s => !spansSeen.Contains(s)).ToList();
                if (agreed.Count > 0 && agreed[^1].Spans[0] < 26)
                {
                    for (var kib = agreed[^1].Kib + 2; kib <= Math.Min(176, agreed[^1].Kib + 8); kib += 2)
                    {
                        proposal.Add(SweepPlan.Id(content, kib));
                    }
                }

                if (agreed.Count > 0 && agreed[0].Spans[0] > 13)
                {
                    for (var kib = agreed[0].Kib - 2; kib >= Math.Max(48, agreed[0].Kib - 8); kib -= 2)
                    {
                        proposal.Add(SweepPlan.Id(content, kib));
                    }
                }

                var (cls, last, first) = Classify(presence, series, notReproducible);
                limits[(markup, content)] = (cls, last, first, presence);
                perContent[content] = new JsonObject
                {
                    ["class"] = cls,
                    ["bracket"] = cls is "bracketed" or "bracketed-wide" ? $"end link present up to {last} pages, absent from {first} pages" : null,
                    ["lastPresentSpan"] = last,
                    ["firstAbsentSpan"] = first,
                    ["countedRuns"] = series.Count,
                    ["spans"] = new JsonObject([.. presence.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value))]),
                    ["sizes"] = new JsonArray([.. bySize.Select(s => (JsonNode?)new JsonObject
                    {
                        ["kib"] = s.Kib,
                        ["fixture"] = s.Fixture,
                        ["spans"] = new JsonArray([.. s.Spans.Select(x => (JsonNode?)x)]),
                        ["endLink"] = s.Present.Count == 1 ? (s.Present[0] ? "present" : "absent") : "differs between repeats",
                    })]),
                    ["coverage"] = new JsonObject
                    {
                        ["met"] = gaps.Count == 0 && missing.Count == 0,
                        ["gaps"] = new JsonArray([.. gaps.Select(g => (JsonNode?)g)]),
                        ["missingSpans13to25"] = new JsonArray([.. missing.Select(m => (JsonNode?)m)]),
                        ["largestSpan"] = presence.Count == 0 ? null : presence.Keys.Max(),
                        ["smallestSpan"] = presence.Count == 0 ? null : presence.Keys.Min(),
                    },
                };
            }

            perContent["spanAlone"] = SpanAlone(limits[(markup, SweepPlan.Uniq)], limits[(markup, SweepPlan.Rep)]);
            classes[markup] = perContent;
        }

        // Runs within ±2 pages of a bracket that were not counted: named.
        var nearLimitNotCounted = new JsonArray();
        foreach (var f in measured.Where(f => f.NotCounted is not null && f.Span is not null))
        {
            var l = limits[(f.Markup, f.Content)];
            if ((l.Last is { } last && Math.Abs(f.Span!.Value - last) <= 2) || (l.First is { } first && Math.Abs(f.Span!.Value - first) <= 2))
            {
                nearLimitNotCounted.Add($"{f.Run} (span {f.Span}): {f.NotCounted}");
            }
        }

        // The offset observation (tabulated only).
        var observation = new JsonArray([.. counted.Where(f => f.EndPresent == true && f.Repetition == 1 && f.EndFields is not null)
            .OrderBy(f => f.Markup, StringComparer.Ordinal).ThenBy(f => f.Content, StringComparer.Ordinal).ThenBy(f => f.Kib)
            .Select(f => (JsonNode?)new JsonObject
            {
                ["run"] = f.Run,
                ["span"] = f.Span,
                ["fragmentIndex"] = f.EndFields!["fragmentIndex"]?.DeepClone(),
                ["fragments"] = f.EndFields!["fragments"]?.DeepClone(),
                ["offsetYPt"] = f.EndFields!["offsetYPt"]?.DeepClone(),
                ["linesAbove"] = f.EndFields!["linesAbove"]?.DeepClone(),
                ["endOnRowLastPage"] = f.EndOnRowLastPage,
            })]);

        var summary = new JsonObject
        {
            ["rounds"] = rounds,
            ["chromiumVersions"] = new JsonArray([.. versions.Select(v => (JsonNode?)v)]),
            ["instrument"] = new JsonObject
            {
                ["problems"] = new JsonArray([.. instrument.Select(p => (JsonNode?)p)]),
                ["stopAndAsk"] = new JsonArray([.. stops.Distinct(StringComparer.Ordinal).Select(p => (JsonNode?)p)]),
                ["measuredRuns"] = measured.Count,
                ["countedRuns"] = counted.Count,
                ["notCounted"] = new JsonArray([.. measured.Where(f => f.NotCounted is not null).Select(f => (JsonNode?)$"{f.Run}: {f.NotCounted}")]),
                ["notCountedNearALimit"] = nearLimitNotCounted,
                ["readersDisagreeOnPresence"] = presenceDisagreement,
                ["readersDisagreeOnPageOnly"] = new JsonArray([.. pageOnly.Select(p => (JsonNode?)p)]),
                ["runsWithEscapes"] = new JsonArray([.. escapes.Select(p => (JsonNode?)p)]),
                ["cappedPasses"] = new JsonArray([.. caps.Select(p => (JsonNode?)p)]),
                ["argvMatchesEarlierDriver"] = runs.All(f => f.ArgvMatches),
            },
            ["reproduction"] = reproduction,
            ["repeatAgreement"] = new JsonObject
            {
                ["fixtureMarkups"] = groups.Count,
                ["notReproducible"] = new JsonArray([.. notReproducible.Select(kv => (JsonNode?)$"{kv.Key.Fixture} {kv.Key.Markup}: {string.Join(" / ", kv.Value.Select(f => $"r{f.Repetition} {f.Signature}"))}")]),
                ["incomplete"] = new JsonArray([.. incomplete.Select(p => (JsonNode?)p)]),
            },
            ["classes"] = classes,
            ["offsetObservation"] = observation,
            ["proposedSecondRound"] = new JsonArray([.. proposal.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(p => (JsonNode?)p)]),
        };

        Directory.CreateDirectory(o["out"]);
        File.WriteAllText(Path.Combine(o["out"], "summary.json"), summary.ToJsonString(Indented) + "\n");
        File.WriteAllText(Path.Combine(o["out"], "runs.csv"), Csv(runs));
        if (o.TryGetValue("propose", out var proposePath))
        {
            File.WriteAllText(proposePath, JsonSerializer.Serialize(proposal.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()) + "\n");
        }

        Console.WriteLine($"{runs.Count} runs ({measured.Count} measured, {counted.Count} counted); stop-and-ask items: {stops.Distinct(StringComparer.Ordinal).Count()}; instrument problems: {instrument.Count}.");
        foreach (var r in reproduction)
        {
            Console.WriteLine($"{r!["check"]} {r["fixture"]} {r["markup"]}: {r["result"]}");
        }

        foreach (var markup in SweepPlan.Markups)
        {
            foreach (var content in SweepPlan.Contents)
            {
                Console.WriteLine($"{markup} {content}: {classes[markup]![content]!["class"]} {classes[markup]![content]!["bracket"]} (coverage met: {classes[markup]![content]!["coverage"]!["met"]})");
            }

            Console.WriteLine($"{markup} span alone: {classes[markup]!["spanAlone"]!["verdict"]}");
        }

        foreach (var s in stops.Distinct(StringComparer.Ordinal))
        {
            Console.WriteLine($"STOP AND ASK: {s}");
        }

        return 0;
    }

    private static (string Class, int? LastPresent, int? FirstAbsent) Classify(Dictionary<int, string> presence, List<RunFacts> series, Dictionary<(string Fixture, string Markup), List<RunFacts>> notReproducible)
    {
        if (presence.Count == 0)
        {
            return ("no counted run", null, null);
        }

        var presenceDiffersBetweenRepeats = series.GroupBy(f => f.Fixture).Any(g => g.Select(f => f.EndPresent).Distinct().Count() > 1);
        if (presenceDiffersBetweenRepeats)
        {
            return ("not reproducible", null, null);
        }

        if (presence.Values.Contains("mixed"))
        {
            return ("mixed", null, null);
        }

        var spans = presence.Keys.Order().ToList();
        if (spans.All(s => presence[s] == "present"))
        {
            return ("no limit in range", spans[^1], null);
        }

        if (spans.All(s => presence[s] == "absent"))
        {
            return ("absent throughout", null, spans[0]);
        }

        var firstAbsent = spans.First(s => presence[s] == "absent");
        if (spans.Any(s => s > firstAbsent && presence[s] == "present"))
        {
            return ("not monotonic", null, null);
        }

        var lastPresent = spans.Last(s => presence[s] == "present");
        var near = series.Where(f => f.Span is { } s && (Math.Abs(s - lastPresent) <= 2 || Math.Abs(s - firstAbsent) <= 2))
            .Any(f => notReproducible.ContainsKey((f.Fixture, f.Markup)));
        if (near)
        {
            return ("not reproducible", lastPresent, firstAbsent);
        }

        return (firstAbsent == lastPresent + 1 ? "bracketed" : "bracketed-wide", lastPresent, firstAbsent);
    }

    private static JsonObject SpanAlone(
        (string Class, int? Last, int? First, Dictionary<int, string> Presence) uniq,
        (string Class, int? Last, int? First, Dictionary<int, string> Presence) rep)
    {
        var common = uniq.Presence.Keys.Intersect(rep.Presence.Keys).Order().ToList();
        var differing = common.Where(s => uniq.Presence[s] != rep.Presence[s]).ToList();
        var mixed = uniq.Presence.Where(kv => kv.Value == "mixed").Select(kv => $"uniq {kv.Key}")
            .Concat(rep.Presence.Where(kv => kv.Value == "mixed").Select(kv => $"rep {kv.Key}")).ToList();
        var limitsNear = new[] { uniq.Last, uniq.First, rep.Last, rep.First }.Where(x => x is not null).Select(x => x!.Value).ToList();
        string verdict;
        if (differing.Count > 0 || mixed.Count > 0)
        {
            verdict = "not span alone";
        }
        else if (limitsNear.Count > 0 && !common.Any(s => limitsNear.Any(l => Math.Abs(s - l) <= 2)))
        {
            verdict = "undetermined";
        }
        else if (common.Count == 0)
        {
            verdict = "undetermined";
        }
        else
        {
            // The bracket is compared only where a class has one; "no limit in range" and
            // "absent throughout" carry the measured range's edge, which is not a bracket.
            var bracketed = uniq.Class is "bracketed" or "bracketed-wide";
            verdict = uniq.Class == rep.Class && (!bracketed || (uniq.Last == rep.Last && uniq.First == rep.First)) ? "consistent with span alone" : "not span alone";
        }

        return new JsonObject
        {
            ["verdict"] = verdict,
            ["commonSpans"] = new JsonArray([.. common.Select(s => (JsonNode?)s)]),
            ["spansWithDifferentPresence"] = new JsonArray([.. differing.Select(s => (JsonNode?)$"{s}: uniq {uniq.Presence[s]}, rep {rep.Presence[s]}")]),
            ["mixedSpans"] = new JsonArray([.. mixed.Select(s => (JsonNode?)s)]),
            ["classes"] = $"uniq {uniq.Class}, rep {rep.Class}",
        };
    }

    private static JsonObject Reproduce(
        string check, string fixture, string markup, List<RunFacts> runs, Dictionary<string, RunFacts> reference, Dictionary<string, JsonObject> referenceSplitRows, bool? expectEnd)
    {
        var referenceRun = $"{fixture}-{markup}-c-r1";
        var refFacts = reference.GetValueOrDefault(referenceRun);
        var refSplit = referenceSplitRows.GetValueOrDefault(referenceRun);
        var differences = new JsonArray();
        var measured = runs.Where(f => f.Round != "smoke" && f.Fixture == fixture && f.Markup == markup).OrderBy(f => f.Repetition).ToList();
        var subject = measured.Count > 0 ? measured : runs.Where(f => f.Fixture == fixture && f.Markup == markup).ToList();
        foreach (var f in subject)
        {
            void Differ(string field, object? expected, object? actual)
            {
                if (!Equals(expected?.ToString(), actual?.ToString()))
                {
                    differences.Add($"{f.Run} {field}: expected {expected ?? "null"}, got {actual ?? "null"}");
                }
            }

            if (expectEnd is not null)
            {
                Differ("end link present", expectEnd, f.EndPresent);
            }

            if (refFacts is not null)
            {
                Differ("pages", refFacts.Pages, f.Pages);
                Differ("row span", $"{refFacts.FirstPage}-{refFacts.LastPage}", $"{f.FirstPage}-{f.LastPage}");
                Differ("annotations", refFacts.Annotations, f.Annotations);
                Differ("row 50 links", string.Join(" | ", refFacts.Row50Lines), string.Join(" | ", f.Row50Lines));
            }

            if (refSplit is not null && expectEnd == true)
            {
                Differ("rowPages", refSplit["rowPages"]?.ToString(), $"{f.FirstPage}-{f.LastPage}");
                var expected = refSplit["e"] as JsonObject;
                foreach (var field in new[] { "word", "rectanglePage", "wordPage", "offsetYPt", "offsetXPt", "linesAbove", "intraFragmentDistancePt", "headerHeightPt", "fragmentIndex", "fragments", "class" })
                {
                    Differ($"e.{field}", expected?[field]?.ToJsonString(), f.EndFields?[field]?.ToJsonString());
                }
            }
        }

        var reference1 = refFacts is null ? "no reference PDF read" : $"reference {referenceRun}: {refFacts.Pages} pages, row 50 {refFacts.FirstPage}-{refFacts.LastPage}, {refFacts.Annotations} annotations, row 50 links {string.Join(" | ", refFacts.Row50Lines)}";
        return new JsonObject
        {
            ["check"] = check,
            ["fixture"] = fixture,
            ["markup"] = markup,
            ["runs"] = new JsonArray([.. subject.Select(f => (JsonNode?)$"{f.Run}: {f.Pages} pages, row 50 {f.FirstPage}-{f.LastPage} ({f.Span}), end link {(f.EndPresent == true ? f.EndRects : "absent")}, {f.Annotations} annotations")]),
            ["reference"] = reference1,
            ["referenceSplitRow"] = refSplit?.DeepClone(),
            ["result"] = subject.Count == 0 ? "not measured" : check == "new" ? "recorded (no earlier counterpart)" : differences.Count == 0 ? "reproduced" : "not reproduced",
            ["differences"] = differences,
        };
    }

    private static (List<RunFacts> Facts, JsonObject Info) ReadRound(string directory, Dictionary<string, (int Bytes, int Characters, string LastWord)> sizes, List<string> instrument, bool isReference = false)
    {
        var runsDirectory = Path.Combine(directory, "runs");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var facts = new Dictionary<string, RunFacts>(StringComparer.Ordinal);
        var round = isReference ? "reference" : name;
        foreach (var line in File.ReadLines(Path.Combine(runsDirectory, "twopass.jsonl")).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var r = JsonNode.Parse(line)!.AsObject();
            var fixture = r["fixtureId"]!.GetValue<string>();
            var (content, kib) = isReference ? (SweepPlan.Uniq, fixture == "X64K" ? 64 : 128) : SweepPlan.Parse(fixture);
            var group = r["group"]?.GetValue<int>() ?? 0;
            var f = new RunFacts
            {
                Round = isReference ? "reference" : group == 0 ? "smoke" : group.ToString(CultureInfo.InvariantCulture),
                Run = r["run"]!.GetValue<string>(),
                Fixture = fixture,
                Content = content,
                Kib = kib,
                Markup = r["markup"]!.GetValue<string>(),
                Repetition = r["repetition"]?.GetValue<int>() ?? 1,
            };
            if (sizes.TryGetValue(fixture, out var size))
            {
                (f.RecordBytes, f.DescriptionCharacters, f.LastWord) = size;
            }

            var p1 = r["pass1"]!.AsObject();
            var p2 = r["pass2"]!.AsObject();
            f.Pass1 = p1["outcome"]!.GetValue<string>();
            f.Pass1Reason = p1["reason"]?.GetValue<string>();
            f.WallMs = p1["wallMs"]?.GetValue<double>();
            f.PdfMs = p1["pdfMs"]?.GetValue<double>();
            f.ChromiumPeakKiB = p1["memory"]?["chromiumTreePeakRssKiB"]?.GetValue<long>();
            f.ChromiumVersion = p1["chromiumVersion"]?.GetValue<string>();
            f.Escapes = (p1["dialogs"]?.GetValue<int>() ?? 0) + (p1["abortedRequests"]?.GetValue<int>() ?? 0) + (p1["scripts"]?.GetValue<int>() ?? 0);
            f.Reading = p2["outcome"]!.GetValue<string>();
            f.ReadingReason = p2["reason"]?.GetValue<string>();
            var readbackPath = Path.Combine(runsDirectory, "readback", f.Run + ".json");
            if (File.Exists(readbackPath))
            {
                var row = JsonNode.Parse(File.ReadAllText(readbackPath))!["rows"]!.AsArray().Select(n => n!.AsObject()).FirstOrDefault(n => n["seq"]!.GetValue<int>() == TallRow);
                f.ReadingEndMarkers = row?["endMarkers"]?.GetValue<int>();
                f.ReadingEndPage = row?["endRectPage"]?.GetValue<int>() is { } page and > 0 ? page : null;
            }

            facts[f.Run] = f;
        }

        // The command lines, against the earlier driver's argument list for its marker probes.
        var argvPath = Path.Combine(runsDirectory, "argv.jsonl");
        var argvLines = 0;
        var processPaths = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(argvPath))
        {
            var driver = JsonNode.Parse(File.ReadAllText(Path.Combine(runsDirectory, "driver.json")))!.AsObject();
            var font = driver["fontFile"]!.GetValue<string>();
            var host = driver["processPath"]!.GetValue<string>();
            foreach (var line in File.ReadLines(argvPath).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                argvLines++;
                var a = JsonNode.Parse(line)!.AsObject();
                var run = a["run"]!.GetValue<string>();
                var pass = a["pass"]!.GetValue<int>();
                processPaths.Add(a["fileName"]!.GetValue<string>());
                if (!facts.TryGetValue(run, out var f))
                {
                    continue;
                }

                var expected = new List<string>
                {
                    SweepDriver.HarnessDll, pass == 1 ? "layout-one" : "stamp-one", "--fixtures", "/fixtures", "--out", "/out", "--run", run,
                    "--fixture", f.Fixture, "--markup", f.Markup, "--strategy", "cell", "--mutation", "-", "--fault", "-", "--font", font,
                };
                if (pass == 2)
                {
                    expected.Add("--read-only");
                }

                var actual = a["arguments"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                f.ArgvMatches = (pass == 1 || f.ArgvMatches) && actual.SequenceEqual(expected, StringComparer.Ordinal) && a["fileName"]!.GetValue<string>() == host;
            }

            foreach (var f in facts.Values.Where(f => f.Pass1 == "not-run"))
            {
                // A planned run that never started has no command line to compare.
                f.ArgvMatches = true;
            }
        }
        else if (isReference)
        {
            foreach (var f in facts.Values)
            {
                f.ArgvMatches = true;
            }
        }

        // The text checker: pages and the row's printed span.
        var integrity = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "o07", "integrity.json")))!["runs"]!.AsArray();
        foreach (var e in integrity.Select(n => n!.AsObject()))
        {
            var runId = e["runId"]!.GetValue<string>();
            if (!runId.EndsWith(".p1", StringComparison.Ordinal) || !facts.TryGetValue(runId[..^3], out var f))
            {
                continue;
            }

            f.TextVerdict = e["verdict"]?.GetValue<string>();
            f.Pages = e["pages"]?.GetValue<int>();
            var span = e["observations"]?["multiPageRows"]?.AsArray().Select(n => n!.AsObject()).FirstOrDefault(n => n["row"]!.GetValue<int>() == TallRow);
            f.FirstPage = span?["firstPage"]?.GetValue<int>();
            f.LastPage = span?["lastPage"]?.GetValue<int>();
        }

        // The accounting checker's split-row probe: qpdf listing and end-rectangle fields.
        var probePath = Path.Combine(directory, "o09", "probe-split-rows.json");
        var probeRuns = 0;
        if (File.Exists(probePath))
        {
            var probe = JsonNode.Parse(File.ReadAllText(probePath))!.AsObject();
            foreach (var item in probe["runs"]!.AsArray().Select(n => n!.AsObject()))
            {
                if (!facts.TryGetValue(item["run"]!.GetValue<string>(), out var f) || item["a"] is not JsonObject a)
                {
                    continue;
                }

                probeRuns++;
                f.Annotations = a["annotations"]?.GetValue<int>();
                f.Unknown = a["unknown"]?.GetValue<int>();
                f.WrongRows = [.. a["rowsWithWrongMarkerCounts"]!.AsArray().Select(x => x!.GetValue<int>())];
                var counts = new Dictionary<int, (int S, int E, int A)>();
                foreach (var entry in a["qpdfListing"]!.AsArray().Select(x => x!.GetValue<string>()))
                {
                    var m = ListingLine().Match(entry);
                    if (!m.Success)
                    {
                        continue;
                    }

                    var seq = int.Parse(m.Groups["seq"].Value, CultureInfo.InvariantCulture);
                    var kind = m.Groups["kind"].Value[0];
                    var c = counts.GetValueOrDefault(seq);
                    counts[seq] = kind switch { 's' => (c.S + 1, c.E, c.A), 'e' => (c.S, c.E + 1, c.A), _ => (c.S, c.E, c.A + 1) };
                    if (seq == TallRow)
                    {
                        f.Row50.Add(new RowLink(kind, int.Parse(m.Groups["page"].Value, CultureInfo.InvariantCulture), m.Groups["rect"].Value));
                        f.Row50Lines.Add(entry);
                    }
                }

                f.OtherRowFaults = [.. Enumerable.Range(1, 100).Where(s => s != TallRow && (!counts.TryGetValue(s, out var c) || c.S != 1 || c.E < 1 || c.A != 1))];
            }

            foreach (var row in probe["splitRows"]?.AsArray().Select(n => n!.AsObject()) ?? [])
            {
                if (row["row"]!.GetValue<int>() == TallRow && facts.TryGetValue(row["run"]!.GetValue<string>(), out var f))
                {
                    f.EndFields = row["e"] as JsonObject;
                }
            }
        }
        else
        {
            instrument.Add($"{directory}: probe-split-rows.json missing");
        }

        var missingFromProbe = facts.Values.Where(f => f.Pass1 == "ok" && f.Annotations is null).Select(f => f.Run).ToList();
        if (missingFromProbe.Count > 0)
        {
            instrument.Add($"{directory}: {missingFromProbe.Count} run(s) absent from the probe output, first {missingFromProbe[0]}");
        }

        var exitPath = Path.Combine(directory, "o09", "exit.txt");
        var probeExit = File.Exists(exitPath) ? File.ReadAllText(exitPath).Trim() : null;
        if (probeExit is not null && probeExit is not ("0" or "1"))
        {
            instrument.Add($"{directory}: the probe exited {probeExit}");
        }

        var info = new JsonObject
        {
            ["round"] = round,
            ["directory"] = name,
            ["runs"] = facts.Count,
            ["argvLines"] = argvLines,
            ["childProcessPaths"] = new JsonArray([.. processPaths.Select(p => (JsonNode?)p)]),
            ["probeRuns"] = probeRuns,
            ["probeExit"] = probeExit,
        };
        return ([.. facts.Values], info);
    }

    private static Dictionary<string, (int Bytes, int Characters, string LastWord)> ReadSizes(string path)
    {
        var result = new Dictionary<string, (int, int, string)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Skip(1).Where(l => l.Length > 0))
        {
            var p = line.Split('\t');
            result[p[0]] = (int.Parse(p[3], CultureInfo.InvariantCulture), int.Parse(p[4], CultureInfo.InvariantCulture), p[5]);
        }

        return result;
    }

    private static string Csv(List<RunFacts> runs)
    {
        var b = new StringBuilder("round,run,fixture,content,kib,recordBytes,descriptionCharacters,lastWord,markup,repetition,pass1,pass1Reason,wallMs,pdfMs,chromiumPeakMiB,chromiumVersion,"
            + "reading,readingReason,textVerdict,pages,rowFirstPage,rowLastPage,span,annotations,endLink,endRects,endOnRowLastPage,startPage,amountPage,"
            + "offsetYPt,fragmentIndex,fragments,linesAbove,class,readersAgreePresence,readersAgreePage,counted\n");
        foreach (var f in runs.OrderBy(f => f.Round, StringComparer.Ordinal).ThenBy(f => f.Repetition).ThenBy(f => f.Markup, StringComparer.Ordinal).ThenBy(f => f.Content, StringComparer.Ordinal).ThenBy(f => f.Kib))
        {
            string Q(object? v) => v switch
            {
                null => string.Empty,
                double d => d.ToString("0.0", CultureInfo.InvariantCulture),
                bool x => x ? "true" : "false",
                _ => '"' + Convert.ToString(v, CultureInfo.InvariantCulture)!.Replace("\"", "\"\"", StringComparison.Ordinal) + '"',
            };
            string N(JsonNode? n) => n is null ? string.Empty : n.ToJsonString().Trim('"');
            b.Append(string.Join(',', new[]
            {
                Q(f.Round), Q(f.Run), Q(f.Fixture), Q(f.Content), Q(f.Kib), Q(f.RecordBytes), Q(f.DescriptionCharacters), Q(f.LastWord), Q(f.Markup), Q(f.Repetition),
                Q(f.Pass1), Q(f.Pass1Reason), Q(f.WallMs), Q(f.PdfMs), Q(f.ChromiumPeakKiB / 1024), Q(f.ChromiumVersion),
                Q(f.Reading), Q(f.ReadingReason), Q(f.TextVerdict), Q(f.Pages), Q(f.FirstPage), Q(f.LastPage), Q(f.Span), Q(f.Annotations),
                Q(f.EndPresent is null ? null : f.EndPresent.Value ? "present" : "absent"), Q(f.EndRects), Q(f.EndOnRowLastPage), Q(f.StartPage), Q(f.AmountPage),
                N(f.EndFields?["offsetYPt"]), N(f.EndFields?["fragmentIndex"]), N(f.EndFields?["fragments"]), N(f.EndFields?["linesAbove"]), N(f.EndFields?["class"]),
                Q(f.ReadersAgreePresence), Q(f.ReadersAgreePage), Q(f.NotCounted is null ? "yes" : f.NotCounted),
            })).Append('\n');
        }

        return b.ToString();
    }

    [GeneratedRegex(@"^p(?<page>\d+) /Link /URI https://m\.invalid/r/(?<seq>\d+)/(?<kind>[sea]) \[(?<rect>[^\]]*)\]$")]
    private static partial Regex ListingLine();
}
