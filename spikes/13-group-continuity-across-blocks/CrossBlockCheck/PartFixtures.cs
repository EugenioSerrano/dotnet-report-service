using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaginationFixtures;

namespace GroupContinuityCheck;

/// <summary>
/// One report of the experiment: dataset records <see cref="First"/> … First + Count − 1
/// (a group's first record to a group's last record), cut into parts of
/// <see cref="Limit"/> records. H1 parts receive the hand-over (an opening amount, a
/// continuation header, the whole group's total); H0 parts are the shared block
/// construction as it is (independently laid-out parts).
/// </summary>
internal sealed record ReportSpec(string Id, string Case, int First, int Count, int Limit, string Variant, IReadOnlyDictionary<int, string> Descriptions)
{
    public const string Handover = "H1";
    public const string Independent = "H0";

    public int Last => First + Count - 1;

    public IEnumerable<(int Part, int Offset, int Count)> Parts()
    {
        var k = 0;
        for (var o = First; o <= Last; o += Limit)
        {
            yield return (++k, o, Math.Min(Limit, Last - o + 1));
        }
    }

    public string PartId(int part) => Id + "-P" + part.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The "part-fixtures" and "calibrate" commands: the parts of every report and their
/// hand-over, built from the grouped dataset with the shared block construction of
/// spikes/12-group-continuity (linked), after its self-checks; the calibration of the
/// page-coinciding boundary from the calibration layouts' page counts.
/// </summary>
internal static class PartFixtures
{
    public const string ContinuationSuffix = " (cont.)";
    public const string ReportsFileName = "reports.json";
    public const string HandoverFileName = "handover.json";

    /// <summary>The fixtures of spikes/12-group-continuity used here for the controls and the parity runs.</summary>
    public static readonly string[] Reused = ["B100", "K1", "F26K-a", "B1000", "G200", "GE52", "GE61", "GS1200", "GTF300u", "GTL300u"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static List<ReportSpec> Reports(IReadOnlyList<DatasetRecord> records, string tallText, int? pbLimit)
    {
        Require(records[149].Sequence == 150 && records[150].Sequence == 151, "the dataset's sequences are not index + 1 at 149/150");
        var none = new Dictionary<int, string>();
        var reports = new List<ReportSpec>
        {
            new("XB-PROBE", "hand-over probe", 0, 401, 183, ReportSpec.Handover, none),
        };
        foreach (var l in new[] { 180, 181, 182, 183, 184, 185 })
        {
            var @case = l switch { 180 or 181 => "split near a group's end", 182 => "boundary between groups", _ => "split near a group's start" };
            reports.Add(new("XB-L" + l.ToString(CultureInfo.InvariantCulture) + "-H1", @case, 0, 594, l, ReportSpec.Handover, none));
        }

        reports.Add(new("XB-L201-H1", "one-record group at a part end", 0, 594, 201, ReportSpec.Handover, none));
        reports.Add(new("XB-TL150-H1", "over-tall row as a part's last row", 0, 594, 150, ReportSpec.Handover, new Dictionary<int, string> { [150] = tallText }));
        reports.Add(new("XB-TF150-H1", "over-tall row as a part's first row", 0, 594, 150, ReportSpec.Handover, new Dictionary<int, string> { [151] = tallText }));
        reports.Add(new("XB-W250-H1", "group spanning more than one whole block", 594, 1360, 250, ReportSpec.Handover, none));
        reports.Add(new("XB-G500-H1", "group spanning whole blocks, larger", 3152, 2400, 500, ReportSpec.Handover, none));
        if (pbLimit is { } pb)
        {
            reports.Add(new("XB-PB-H1", "block boundary on a page boundary", 594, 1360, pb, ReportSpec.Handover, none));
        }

        reports.Add(new("XB-ALL-H1", "the whole dataset (top of the bracket)", 0, 10000, 1000, ReportSpec.Handover, none));
        reports.Add(new("XB-L185-H0", "independent parts (baseline)", 0, 594, 185, ReportSpec.Independent, none));
        reports.Add(new("XB-W250-H0", "independent parts (baseline)", 594, 1360, 250, ReportSpec.Independent, none));
        foreach (var r in reports)
        {
            Require(r.First == 0 || records[r.First - 1].GroupId != records[r.First].GroupId, $"{r.Id}: record {r.First} is not a group's first record");
            Require(r.Last == records.Count - 1 || records[r.Last + 1].GroupId != records[r.Last].GroupId, $"{r.Id}: record {r.Last} is not a group's last record");
        }

        return reports;
    }

    public static int Run(string datasetPath, string sumsPath, string referenceDirectory, string tallPath, string groupReferencePath, string? calibrationPath, int calibrationFrom, int calibrationTo, string outDirectory)
    {
        Dataset dataset;
        try
        {
            dataset = Dataset.Load(datasetPath, sumsPath);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        var records = dataset.Records;
        Console.WriteLine($"Dataset {dataset.FileName} sha256 {dataset.Sha256} ({records.Count} records) verified.");

        // Self-check 1: the linked builder rebuilds the fixture set of spikes/12-group-continuity with its committed SUMS.
        var rebuild = Path.Combine(outDirectory, "group-continuity-rebuild");
        if (GroupFixtures.Run(datasetPath, sumsPath, referenceDirectory, tallPath, rebuild) != 0)
        {
            Console.Error.WriteLine("Self-check 1 failed: the linked builder did not rebuild the earlier fixture set.");
            return 1;
        }

        var rebuilt = File.ReadAllBytes(Path.Combine(rebuild, "fixtures", FixtureFile.SumsFileName));
        if (!rebuilt.AsSpan().SequenceEqual(File.ReadAllBytes(groupReferencePath)))
        {
            Console.Error.WriteLine($"Self-check 1 failed: the rebuilt SHA256SUMS differs from {groupReferencePath}.");
            return 1;
        }

        Console.WriteLine($"Self-check 1: the earlier fixture set rebuilt with its committed SHA256SUMS ({Sums.HashFile(groupReferencePath)}).");
        var rebuiltSums = Sums.Read(Path.Combine(rebuild, "fixtures", FixtureFile.SumsFileName));

        var referenceSums = Sums.Read(Path.Combine(referenceDirectory, FixtureFile.SumsFileName));
        var tall = FixtureFile.ReadVerified(Path.GetDirectoryName(tallPath)!, Path.GetFileNameWithoutExtension(tallPath), referenceSums);
        var tallText = tall.Items.First(r => r.Sequence == 50).Description!;

        int? pbLimit = null;
        if (calibrationPath is not null)
        {
            pbLimit = JsonNode.Parse(File.ReadAllText(calibrationPath))!["blockLimit"]?.GetValue<int?>();
            if (pbLimit is null)
            {
                Console.Error.WriteLine($"{calibrationPath} holds no block limit.");
                return 1;
            }
        }

        List<ReportSpec> reports;
        try
        {
            reports = Reports(records, tallText, pbLimit);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        var first = Path.Combine(outDirectory, "fixtures");
        var second = Path.Combine(outDirectory, "fixtures-second-build");
        double handoverMs = 0;
        foreach (var directory in new[] { first, second })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
            foreach (var id in Reused)
            {
                _ = FixtureFile.ReadVerified(Path.Combine(rebuild, "fixtures"), id, rebuiltSums);
                File.Copy(FixtureFile.PathOf(Path.Combine(rebuild, "fixtures"), id), FixtureFile.PathOf(directory, id));
            }

            var openings = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var reportNodes = new JsonArray();
            var watch = new Stopwatch();
            foreach (var spec in reports)
            {
                var (node, parts) = BuildReport(records, spec, openings, watch);
                foreach (var fixture in parts)
                {
                    FixtureFile.Write(directory, fixture);
                }

                reportNodes.Add(node);
            }

            // The calibration layouts: part 1 of records 594 … 594 + L − 1 (as part 1 of the page-coinciding report).
            for (var l = calibrationFrom; l <= calibrationTo; l++)
            {
                var id = "XB-CAL-L" + l.ToString(CultureInfo.InvariantCulture);
                FixtureFile.Write(directory, GroupFixtures.Build(records, id, 594, l, Fixture.WhiteSpaceNormal, new Dictionary<int, string>()));
                openings[id] = "0.00";
            }

            handoverMs = watch.Elapsed.TotalMilliseconds;
            WriteJson(Path.Combine(directory, ReportsFileName), new JsonObject { ["dataset"] = dataset.FileName, ["reports"] = reportNodes });
            WriteJson(Path.Combine(directory, HandoverFileName), new JsonObject { ["openings"] = new JsonObject(openings.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))) });
            Sums.WriteFolder(directory, Path.Combine(directory, FixtureFile.SumsFileName));
        }

        var a = File.ReadAllBytes(Path.Combine(first, FixtureFile.SumsFileName));
        var b = File.ReadAllBytes(Path.Combine(second, FixtureFile.SumsFileName));
        if (!a.AsSpan().SequenceEqual(b))
        {
            Console.Error.WriteLine("The two builds differ: the fixtures are not deterministic.");
            return 1;
        }

        Directory.Delete(second, recursive: true);
        var sumsHash = Sums.HashFile(Path.Combine(first, FixtureFile.SumsFileName));
        Console.WriteLine($"Two builds identical: {FixtureFile.SumsFileName} {sumsHash} ({Directory.GetFiles(first).Length} files).");

        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(first, ReportsFileName)))!.AsObject();
        report["sha256"] = dataset.Sha256;
        report["records"] = records.Count;
        report["selfChecks"] = new JsonArray(
            "the linked builder rebuilt the fixture set of spikes/12-group-continuity with its committed SHA256SUMS",
            "every report's parts hold exactly its dataset records, in order, each once",
            "every independent (H0) part is byte-identical to the shared block construction of its slice",
            "two builds identical");
        report["fixtureSumsSha256"] = sumsHash;
        report["calibration"] = calibrationPath is null ? null : new JsonObject { ["file"] = Path.GetFileName(calibrationPath), ["blockLimit"] = pbLimit };
        report["handoverComputeMs"] = Math.Round(handoverMs, 2);
        report["handoverComputeNote"] = "time to compute every report's openings, continuation headers and whole-group totals from the source data (both builds' second pass)";
        WriteJson(Path.Combine(outDirectory, "report-fixtures.json"), report);
        foreach (var r in reports)
        {
            var parts = r.Parts().Select(p => p.Count.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine($"{r.Id}: records {r.First}-{r.Last} ({r.Count}), block limit {r.Limit}, parts {string.Join("/", parts)}, {r.Variant}");
        }

        return 0;
    }

    private static (JsonObject Node, List<Fixture> Parts) BuildReport(IReadOnlyList<DatasetRecord> records, ReportSpec report, IDictionary<string, string> openings, Stopwatch watch)
    {
        var parts = new List<Fixture>();
        var partNodes = new JsonArray();
        var boundaries = new JsonArray();
        var concatenated = new List<int>();
        foreach (var (k, offset, count) in report.Parts())
        {
            var id = report.PartId(k);
            var shared = GroupFixtures.Build(records, id, offset, count, Fixture.WhiteSpaceNormal, report.Descriptions);
            var fixture = shared;
            if (report.Variant == ReportSpec.Handover)
            {
                watch.Start();
                fixture = HandOver(records, report, offset, shared);
                openings[id] = Amount(records.Skip(report.First).Take(offset - report.First).Sum(r => r.Amount));
                watch.Stop();
            }
            else
            {
                Require(FixtureFile.Serialize(fixture).AsSpan().SequenceEqual(FixtureFile.Serialize(GroupFixtures.Build(records, id, offset, count, Fixture.WhiteSpaceNormal, report.Descriptions))),
                    $"{id}: an independent part differs from the shared construction");
            }

            parts.Add(fixture);
            concatenated.AddRange(fixture.Items.Select(i => i.Sequence!.Value));
            var groups = SourceGroups.Of(fixture);
            partNodes.Add(new JsonObject
            {
                ["fixture"] = id,
                ["part"] = k,
                ["records"] = new JsonObject { ["first"] = offset, ["last"] = offset + count - 1, ["count"] = count },
                ["groups"] = new JsonArray([.. groups.Select(g => (JsonNode)new JsonObject
                {
                    ["id"] = g.Id,
                    ["items"] = g.Items.Count,
                    ["firstSequence"] = g.Items[0].Sequence,
                    ["lastSequence"] = g.Items[^1].Sequence,
                    ["openAtStart"] = offset > 0 && records[offset - 1].GroupId == g.Id,
                    ["closedInPart"] = g.Closed,
                })]),
            });
            if (k > 1)
            {
                boundaries.Add(Boundary(records, k, offset, count));
            }
        }

        var expected = records.Skip(report.First).Take(report.Count).Select(r => r.Sequence).ToList();
        Require(concatenated.SequenceEqual(expected), $"{report.Id}: the parts do not hold exactly the report's records in order");
        var node = new JsonObject
        {
            ["report"] = report.Id,
            ["case"] = report.Case,
            ["variant"] = report.Variant,
            ["records"] = new JsonObject { ["first"] = report.First, ["last"] = report.Last, ["count"] = report.Count },
            ["blockLimit"] = report.Limit,
            ["parts"] = partNodes,
            ["boundaries"] = boundaries,
            ["changedDescriptions"] = new JsonArray([.. report.Descriptions.Select(kv => (JsonNode)new JsonObject { ["sequence"] = kv.Key, ["characters"] = kv.Value.Length })]),
        };
        return (node, parts);
    }

    /// <summary>
    /// The H1 hand-over, at data level only: a part that starts inside a group prints that
    /// group's header with " (cont.)"; a group that ends in this part but started in an
    /// earlier one prints the whole group's total. Nothing else of the shared construction changes.
    /// </summary>
    private static Fixture HandOver(IReadOnlyList<DatasetRecord> records, ReportSpec report, int offset, Fixture shared)
    {
        var rows = shared.Rows.ToList();
        if (offset > report.First && records[offset - 1].GroupId == records[offset].GroupId)
        {
            Require(rows[0].Kind == RowKind.GroupStart && rows[0].GroupId == records[offset].GroupId, $"{shared.Id}: no group-start row first");
            rows[0] = rows[0] with { GroupName = rows[0].GroupName + ContinuationSuffix };
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind != RowKind.GroupEnd)
            {
                continue;
            }

            var lastSequence = rows[i - 1].Sequence!.Value;
            var end = IndexOfSequence(records, lastSequence);
            var start = end;
            while (start > 0 && records[start - 1].GroupId == records[end].GroupId)
            {
                start--;
            }

            if (start < offset)
            {
                rows[i] = rows[i] with { Total = Amount(records.Skip(start).Take(end - start + 1).Sum(r => r.Amount)) };
            }
        }

        return shared with { Rows = rows };
    }

    private static JsonObject Boundary(IReadOnlyList<DatasetRecord> records, int part, int offset, int count)
    {
        var node = new JsonObject { ["beforePart"] = part, ["firstRecord"] = offset };
        var g = records[offset].GroupId;
        if (records[offset - 1].GroupId != g)
        {
            node["class"] = "between";
            node["openGroup"] = null;
            node["closingGroup"] = records[offset - 1].GroupId;
            return node;
        }

        var start = offset;
        while (start > 0 && records[start - 1].GroupId == g)
        {
            start--;
        }

        var end = offset;
        while (end + 1 < records.Count && records[end + 1].GroupId == g)
        {
            end++;
        }

        var before = offset - start;
        var after = end - offset + 1;
        node["class"] = before <= 3 ? "near-start" : after <= 3 ? "near-end" : "middle";
        node["openGroup"] = g;
        node["recordsBefore"] = before;
        node["recordsAfter"] = after;
        node["spansWholePart"] = end >= offset + count - 1;
        return node;
    }

    private static int IndexOfSequence(IReadOnlyList<DatasetRecord> records, int sequence)
    {
        var i = sequence - 1;
        Require(i >= 0 && i < records.Count && records[i].Sequence == sequence, $"sequence {sequence} is not at index {i}");
        return i;
    }

    public static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    public static void WriteJson(string path, JsonNode node) =>
        File.WriteAllText(path, node.ToJsonString(Indented) + "\n");

    /// <summary>The "calibrate" command: page counts of the calibration layouts, read from their PDFs with poppler, and the block limit L*.</summary>
    public static async Task<int> CalibrateAsync(string runsDirectory, int from, int to, string outPath, int timeoutSeconds, CancellationToken cancellation)
    {
        var stage = StrictAccountingCheck.StageFolder.Load(runsDirectory);
        var pages = new SortedDictionary<int, int?>();
        foreach (var planned in stage.Plan.Runs)
        {
            var l = int.Parse(planned.FixtureId["XB-CAL-L".Length..], CultureInfo.InvariantCulture);
            var run = stage.Runs.GetValueOrDefault(planned.RunId);
            pages[l] = run is { Outcome: "ok", Pdf: not null }
                ? await TextIntegrityCheck.Poppler.PageCountAsync(Path.Combine(stage.Directory, run.Pdf), TimeSpan.FromSeconds(timeoutSeconds), cancellation)
                : null;
        }

        int? limit = null;
        for (var l = from; l < to; l++)
        {
            if (pages.GetValueOrDefault(l) is { } p && pages.GetValueOrDefault(l + 1) is { } q && q == p + 1)
            {
                limit = l;
                break;
            }
        }

        WriteJson(outPath, new JsonObject
        {
            ["rule"] = "the smallest L in the range with pages(L + 1) = pages(L) + 1: record L + 1 would start a new page, so part 1 ends exactly at a page end",
            ["range"] = new JsonArray(from, to - 1),
            ["records"] = "594 … 594 + L − 1 (part 1 of the page-coinciding report)",
            ["pages"] = new JsonObject(pages.Select(kv => KeyValuePair.Create(kv.Key.ToString(CultureInfo.InvariantCulture), (JsonNode?)JsonValue.Create(kv.Value)))),
            ["blockLimit"] = limit,
            ["note"] = "a documented exception: this block limit is chosen from layout results (page counts), before the measured stage",
        });
        Console.WriteLine($"Calibration: pages {string.Join(" ", pages.Select(kv => $"{kv.Key}:{kv.Value?.ToString(CultureInfo.InvariantCulture) ?? "-"}"))}; block limit {(limit?.ToString(CultureInfo.InvariantCulture) ?? "none")}.");
        return limit is null ? 1 : 0;
    }
}
