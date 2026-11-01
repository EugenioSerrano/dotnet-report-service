using System.Globalization;
using System.Text.Json.Nodes;
using PaginationFixtures;

namespace GroupContinuityCheck;

/// <summary>The verify items of folder 06's verifier, as its report writes them.</summary>
internal sealed record VerifyItems(bool Exists, bool Structure, string Boxes, bool Order, bool Numbering, bool Pixels, int? FirstFailingPage, string? FirstFailure, int? Pages)
{
    public bool Pass => Exists && Structure && Boxes != "fail" && Order && Numbering && Pixels;

    public static VerifyItems Read(string path)
    {
        if (!File.Exists(path))
        {
            return new VerifyItems(false, false, "missing", false, false, false, null, "no verify report", null);
        }

        var n = JsonNode.Parse(File.ReadAllText(path))!;
        return new VerifyItems(true, n["structurePass"]!.GetValue<bool>(), n["boxes"]!["verdict"]!.GetValue<string>(), n["orderPass"]!.GetValue<bool>(),
            n["numberingPass"]!.GetValue<bool>(), n["pixelPass"]!.GetValue<bool>(), n["firstFailingPage"]?.GetValue<int?>(), n["firstFailure"]?.GetValue<string>(), n["pageCount"]?.GetValue<int>());
    }

    public JsonObject ToJson() => new()
    {
        ["exists"] = Exists,
        ["structure"] = Structure,
        ["boxes"] = Boxes,
        ["order"] = Order,
        ["numbering"] = Numbering,
        ["pixels"] = Pixels,
        ["pass"] = Pass,
        ["firstFailingPage"] = FirstFailingPage,
        ["firstFailure"] = FirstFailure,
        ["pages"] = Pages,
    };
}

/// <summary>
/// The "report" command: for every assembled instance, the report-level checks (XA, RG),
/// the chain at every boundary (PB-7), the boundary observations (PB-1 … PB-7), folder
/// 06's verify items for the numbered and the unnumbered report, the hash of the
/// numbered report's words, and — for the hand-over probe — its known answers.
/// </summary>
internal static class ReportCommand
{
    public static async Task<int> RunAsync(string fixturesDirectory, string slicesDirectory, string reportsDirectory, string outDirectory, bool observationsOnly, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var boundaries = JsonNode.Parse(File.ReadAllText(Path.Combine(fixturesDirectory, PartFixtures.ReportsFileName)))!["reports"]!.AsArray()
            .ToDictionary(r => r!["report"]!.GetValue<string>(), r => r!["boundaries"]!.AsArray(), StringComparer.Ordinal);
        var reports = new JsonArray();
        var observations = new JsonArray();
        JsonObject? probe = null;
        foreach (var instance in Instances.Read(reportsDirectory))
        {
            var node = new JsonObject { ["instance"] = instance.Instance, ["report"] = instance.Report, ["repetition"] = instance.Repetition, ["variant"] = instance.Variant };
            reports.Add(node);
            if (!instance.Complete)
            {
                node["verdict"] = "inconclusive";
                node["reason"] = instance.Reason;
                continue;
            }

            var folder = Path.Combine(reportsDirectory, instance.Instance);
            var numbered = VerifyItems.Read(Path.Combine(folder, "verify-numbered.json"));
            var unnumbered = VerifyItems.Read(Path.Combine(folder, "verify-unnumbered.json"));
            node["verifyNumbered"] = numbered.ToJson();
            node["verifyUnnumbered"] = unnumbered.ToJson();
            node["assembly"] = ReadAssembly(folder);
            List<PartDoc> parts;
            try
            {
                parts = await Instances.LoadAsync(instance, slicesDirectory, fixturesDirectory, sums, timeout, cancellation);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                node["verdict"] = "no-check";
                node["reason"] = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
                continue;
            }

            var result = ReportChecks.Check(instance.Instance, parts);
            var observed = ReportChecks.Observe(instance, parts, boundaries[instance.Report], result);
            observations.Add(observed);
            node["verdict"] = "checked";
            node["pages"] = result.Pages;
            node["accountingRules"] = new JsonArray([.. result.Rules.Select(r => (JsonNode)new JsonObject { ["rule"] = $"XA-{r.Rule}", ["name"] = r.Name, ["outcome"] = r.Outcome, ["page"] = r.Page, ["detail"] = r.Detail })]);
            node["accountingFailures"] = new JsonArray([.. result.AccountingFailures.Select(f => (JsonNode)f)]);
            node["groupFailures"] = new JsonArray([.. result.GroupFailures.Select(f => (JsonNode)new JsonObject { ["rule"] = f.Rule, ["class"] = f.Class, ["group"] = f.Group, ["row"] = f.Row, ["page"] = f.Page, ["detail"] = f.Detail })]);
            node["boundaryChain"] = observed["pb7Chain"]!.DeepClone();
            node["numberedWordsSha256"] = File.Exists(Path.Combine(folder, Instances.NumberedFile))
                ? await Instances.WordsSha256Async(Path.Combine(folder, Instances.NumberedFile), timeout, cancellation)
                : null;
            Console.WriteLine($"{instance.Instance}: {result.Pages} pages; verify numbered {(numbered.Pass ? "pass" : "FAIL")}, unnumbered {(unnumbered.Pass ? "pass" : "FAIL")}; "
                + $"XA [{string.Join(" ", result.AccountingFailures)}]; RG [{string.Join(" ", result.GroupFailures.Select(f => f.Key).Distinct())}]; "
                + $"PB-7 {string.Join(" ", result.Boundaries.Select(b => b.State[0]))}");
            if (instance.Report == "XB-PROBE")
            {
                probe = Probe(instance, result, observed, numbered, unnumbered);
            }
        }

        if (!observationsOnly)
        {
            PartFixtures.WriteJson(Path.Combine(outDirectory, "report.json"), new JsonObject { ["instances"] = reports });
        }

        PartFixtures.WriteJson(Path.Combine(outDirectory, "observations.json"), new JsonObject { ["instances"] = observations });
        if (probe is not null)
        {
            PartFixtures.WriteJson(Path.Combine(outDirectory, "probe.json"), probe);
            Console.WriteLine($"Hand-over probe: {(probe["holds"]!.GetValue<bool>() ? "holds" : "DOES NOT HOLD")}.");
        }

        // The binding note of the review: the numbering area is empty on every page of the longest-title parts before stamping.
        var notes = new JsonArray();
        foreach (var name in new[] { "XB-L180-H1-r1", "XB-TL150-H1-r1" })
        {
            var verify = VerifyItems.Read(Path.Combine(reportsDirectory, name, "verify-unnumbered.json"));
            if (verify.Exists)
            {
                notes.Add(new JsonObject { ["instance"] = name, ["numberingAreaEmptyOnEveryPage"] = verify.Numbering, ["unnumberedPass"] = verify.Pass, ["numberedPass"] = VerifyItems.Read(Path.Combine(reportsDirectory, name, "verify-numbered.json")).Pass });
            }
        }

        PartFixtures.WriteJson(Path.Combine(outDirectory, "numbering-area.json"), new JsonObject { ["instances"] = notes });
        return 0;
    }

    private static JsonObject? ReadAssembly(string folder)
    {
        var path = Path.Combine(folder, "assembly.txt");
        if (!File.Exists(path))
        {
            return null;
        }

        var node = new JsonObject();
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2)
            {
                node[parts[0]] = parts[1];
            }
        }

        return node;
    }

    /// <summary>The known answers of the hand-over probe (records 0 … 400, block limit 183, three parts).</summary>
    private static JsonObject Probe(ReportInstance instance, ReportResult result, JsonObject observed, VerifyItems numbered, VerifyItems unnumbered)
    {
        var answers = new JsonArray();
        var all = true;
        void Answer(string known, bool holds, string detail)
        {
            all &= holds;
            answers.Add(new JsonObject { ["knownAnswer"] = known, ["holds"] = holds, ["observed"] = detail });
        }

        var chain = result.Boundaries;
        Answer("each part's first brought forward equals the previous part's last carried forward",
            chain.Count == 2 && chain.All(b => b.State == "continuous"), string.Join("; ", chain.Select(b => $"before part {b.BeforePart}: {b.CarriedBefore} / {b.BroughtAfter} {b.State}")));
        var b2 = observed["behaviour2AtBoundary"]!.AsArray();
        Answer("G0002 prints its plain header once (part 1) and its continuation header at the start of parts 2 and 3",
            b2.Count == 2 && b2.All(b => b!["group"]!.GetValue<string>() == "G0002" && b["produced"]!.GetValue<bool>())
            && !result.GroupFailures.Any(f => f.Group == "G0002" && f.Rule == "RG-1"),
            string.Join("; ", b2.Select(b => $"before part {b!["beforePart"]}: {b["firstLine"]}")));
        Answer("every group total prints once with its whole group's source sum (G0001 in part 1, G0002 in part 3), every row in its group, every part total exact",
            result.GroupFailures.Count == 0, string.Join(" ", result.GroupFailures.Select(f => f.Key)));
        Answer("the accounting rules pass across the parts", result.AccountingFailures.Count == 0, string.Join(" ", result.AccountingFailures));
        Answer("folder 06's verify passes on the numbered and on the unnumbered report", numbered.Pass && unnumbered.Pass,
            $"numbered {(numbered.Pass ? "pass" : numbered.FirstFailure)}, unnumbered {(unnumbered.Pass ? "pass" : unnumbered.FirstFailure)}");
        return new JsonObject { ["instance"] = instance.Instance, ["holds"] = all, ["answers"] = answers };
    }

    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
