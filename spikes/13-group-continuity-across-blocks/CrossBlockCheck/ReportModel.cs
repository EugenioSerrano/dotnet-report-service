using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using PaginationFixtures;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace GroupContinuityCheck;

/// <summary>One part of an assembled report instance: its fixture, its run and its pages in the report.</summary>
internal sealed record InstancePart(string Fixture, string Run, string Pdf, int Pages, int FirstGlobalPage);

/// <summary>One assembled report: a report's parts with one repetition, in order (instances.json).</summary>
internal sealed record ReportInstance(string Instance, string Report, int Repetition, string Variant, bool Complete, string? Reason, List<InstancePart> Parts)
{
    public int TotalPages => Parts.Sum(p => p.Pages);
}

/// <summary>A part as the checks read it: its source fixture and the printed document of its slice of the assembled report.</summary>
internal sealed class PartDoc
{
    public required InstancePart Part { get; init; }

    public required Fixture Fixture { get; init; }

    public required PrintedDocument Document { get; init; }

    public PdfReading Read() => PdfReading.From(Document, Fixture, "expected", "accounting");

    public PartDoc With(PrintedDocument document) => new() { Part = Part, Fixture = Fixture, Document = document };
}

internal static class Instances
{
    public const string FileName = "instances.json";
    public const string SetFileName = "set.json";
    public const string NumberedFile = "numbered.pdf";
    public const string UnnumberedFile = "unnumbered.pdf";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static List<ReportInstance> Read(string reportsDirectory) =>
        JsonSerializer.Deserialize<List<ReportInstance>>(File.ReadAllText(Path.Combine(reportsDirectory, FileName)), Json) ?? [];

    public static string SliceId(string instance, int part) => $"{instance}-P{part.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The "set-manifest" command: for every report of reports.json and every repetition the
    /// stage ran, one instance folder with folder 06's set manifest (parts in order, their
    /// page counts read with poppler, their hashes), and instances.json.
    /// </summary>
    public static async Task<int> SetManifestsAsync(string fixturesDirectory, string runsDirectory, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var stage = StageFolder.Load(runsDirectory);
        var reports = JsonNode.Parse(File.ReadAllText(Path.Combine(fixturesDirectory, PartFixtures.ReportsFileName)))!["reports"]!.AsArray();
        var instances = new List<ReportInstance>();
        foreach (var report in reports)
        {
            var id = report!["report"]!.GetValue<string>();
            var variant = report["variant"]!.GetValue<string>();
            var fixtures = report["parts"]!.AsArray().Select(p => p!["fixture"]!.GetValue<string>()).ToList();
            var repetitions = stage.Plan.Runs.Where(p => p.FixtureId == fixtures[0]).Select(p => p.Repetition).Distinct().Order().ToList();
            foreach (var repetition in repetitions)
            {
                var instance = $"{id}-r{repetition.ToString(CultureInfo.InvariantCulture)}";
                var parts = new List<InstancePart>();
                string? reason = null;
                var next = 1;
                foreach (var fixture in fixtures)
                {
                    var planned = stage.Plan.Runs.FirstOrDefault(p => p.FixtureId == fixture && p.Repetition == repetition);
                    var run = planned is null ? null : stage.Runs.GetValueOrDefault(planned.RunId);
                    if (run is not { Outcome: "ok", Pdf: not null })
                    {
                        reason ??= $"{fixture}: {(planned is null ? "not planned" : run?.Outcome ?? "no run record")}";
                        continue;
                    }

                    var pdf = Path.Combine(stage.Directory, run.Pdf);
                    var pages = await Poppler.PageCountAsync(pdf, TimeSpan.FromSeconds(timeoutSeconds), cancellation);
                    parts.Add(new InstancePart(fixture, run.RunId, Path.GetFullPath(pdf), pages, next));
                    next += pages;
                }

                var complete = reason is null;
                instances.Add(new ReportInstance(instance, id, repetition, variant, complete, reason, parts));
                var folder = Path.Combine(outDirectory, instance);
                Directory.CreateDirectory(folder);
                if (complete)
                {
                    WriteSet(Path.Combine(folder, SetFileName), parts, swap: false);
                    if (instance == ReportControls.NumberingBase)
                    {
                        WriteSet(Path.Combine(folder, "set-swapped.json"), parts, swap: true);
                    }
                }

                Console.WriteLine($"{instance}: {(complete ? $"{parts.Count} parts, {parts.Sum(p => p.Pages)} pages" : "incomplete — " + reason)}");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outDirectory, FileName), JsonSerializer.Serialize(instances, Json) + "\n", cancellation);
        return 0;
    }

    /// <summary>
    /// Folder 06's set manifest (camel case): seed 0, the target pages, every part with its file
    /// name and its hash. The assembly harness reads each part from the manifest's folder and
    /// builds its rounded reference under the same name, so every part is copied there, byte for
    /// byte, under its run id.
    /// </summary>
    private static void WriteSet(string path, List<InstancePart> parts, bool swap)
    {
        var order = parts.ToList();
        if (swap)
        {
            (order[1], order[2]) = (order[2], order[1]);
        }

        var folder = Path.GetDirectoryName(path)!;
        var entries = new JsonArray();
        var first = 1;
        for (var i = 0; i < order.Count; i++)
        {
            var p = order[i];
            var name = p.Run + ".pdf";
            File.Copy(p.Pdf, Path.Combine(folder, name), overwrite: true);
            entries.Add(new JsonObject
            {
                ["order"] = i + 1,
                ["file"] = name,
                ["pages"] = p.Pages,
                ["firstGlobalPage"] = first,
                ["sha256"] = Sums.HashFile(p.Pdf),
            });
            first += p.Pages;
        }

        PartFixtures.WriteJson(path, new JsonObject { ["seed"] = 0, ["targetPages"] = parts.Sum(p => p.Pages), ["parts"] = entries });
    }

    /// <summary>
    /// The "slices" command: every complete instance's unnumbered report cut back into one PDF
    /// per part (poppler's pdfseparate and pdfunite, by the set manifest's page ranges), and a
    /// runs folder for the text check (plan.json and runs.jsonl: the part's own run line with
    /// the slice's id and PDF).
    /// </summary>
    public static async Task<int> SlicesAsync(string reportsDirectory, string runsDirectory, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var pdfDirectory = Path.Combine(outDirectory, "pdf");
        var work = Path.Combine(outDirectory, "work");
        Directory.CreateDirectory(pdfDirectory);
        var runLines = File.ReadLines(Path.Combine(runsDirectory, "runs.jsonl")).Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonNode.Parse(l)!.AsObject()).GroupBy(n => n["runId"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Last());
        var plan = new JsonArray();
        var lines = new StringBuilder();
        var failures = 0;
        foreach (var instance in Read(reportsDirectory).Where(i => i.Complete))
        {
            var unnumbered = Path.Combine(reportsDirectory, instance.Instance, UnnumberedFile);
            if (!File.Exists(unnumbered))
            {
                Console.WriteLine($"{instance.Instance}: no unnumbered report");
                failures++;
                continue;
            }

            for (var k = 1; k <= instance.Parts.Count; k++)
            {
                var part = instance.Parts[k - 1];
                var id = SliceId(instance.Instance, k);
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }

                Directory.CreateDirectory(work);
                var lastPage = part.FirstGlobalPage + part.Pages - 1;
                var (exit, output) = await Poppler.RunAsync("pdfseparate", ["-f", Text(part.FirstGlobalPage), "-l", Text(lastPage), unnumbered, Path.Combine(work, "page-%d.pdf")], timeout, cancellation);
                var target = Path.Combine(pdfDirectory, id + ".pdf");
                var pages = Enumerable.Range(part.FirstGlobalPage, part.Pages).Select(p => Path.Combine(work, $"page-{Text(p)}.pdf")).ToList();
                if (exit == 0 && pages.Count == 1)
                {
                    File.Copy(pages[0], target, overwrite: true);
                }
                else if (exit == 0)
                {
                    (exit, output) = await Poppler.RunAsync("pdfunite", [.. pages, target], timeout, cancellation);
                }

                if (exit != 0 || await Poppler.PageCountAsync(target, timeout, cancellation) != part.Pages)
                {
                    Console.WriteLine($"{id}: slicing failed (exit {exit}) {output.Split('\n')[0]}");
                    failures++;
                    continue;
                }

                var line = runLines[part.Run].DeepClone().AsObject();
                line["runId"] = id;
                line["pdf"] = "pdf/" + id + ".pdf";
                lines.Append(line.ToJsonString()).Append('\n');
                plan.Add(new JsonObject
                {
                    ["runId"] = id,
                    ["fixtureId"] = part.Fixture,
                    ["markup"] = "grid",
                    ["repetition"] = instance.Repetition,
                    ["split"] = "split",
                    ["footerOn"] = true,
                    ["stage"] = 1,
                    ["matrixRow"] = 0,
                });
            }
        }

        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        PartFixtures.WriteJson(Path.Combine(outDirectory, "plan.json"), new JsonObject { ["matrix"] = "slices", ["runs"] = plan });
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "runs.jsonl"), lines.ToString(), cancellation);
        Console.WriteLine($"{plan.Count} slices written, {failures} failures.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Reads every part of an instance from its slice of the unnumbered report.</summary>
    public static async Task<List<PartDoc>> LoadAsync(ReportInstance instance, string slicesDirectory, string fixturesDirectory, Dictionary<string, string> sums, TimeSpan timeout, CancellationToken cancellation)
    {
        var docs = new List<PartDoc>();
        for (var k = 1; k <= instance.Parts.Count; k++)
        {
            var part = instance.Parts[k - 1];
            var fixture = FixtureFile.ReadVerified(fixturesDirectory, part.Fixture, sums);
            var text = await Poppler.ExtractAsync(Path.Combine(slicesDirectory, "pdf", SliceId(instance.Instance, k) + ".pdf"), timeout, cancellation);
            docs.Add(new PartDoc { Part = part, Fixture = fixture, Document = PrintedDocument.From(text) });
        }

        return docs;
    }

    /// <summary>A one-part "report" read from a run's own PDF (the agreement and parity items).</summary>
    public static async Task<PartDoc> LoadRunAsync(string pdf, string runId, Fixture fixture, TimeSpan timeout, CancellationToken cancellation)
    {
        var text = await Poppler.ExtractAsync(pdf, timeout, cancellation);
        return new PartDoc { Part = new InstancePart(fixture.Id, runId, pdf, text.Pages, 1), Fixture = fixture, Document = PrintedDocument.From(text) };
    }

    /// <summary>SHA-256 of every word of a PDF, page by page in reading order (reproducibility of the numbered report).</summary>
    public static async Task<string> WordsSha256Async(string pdf, TimeSpan timeout, CancellationToken cancellation)
    {
        var text = await Poppler.ExtractAsync(pdf, timeout, cancellation);
        var builder = new StringBuilder();
        foreach (var w in text.Words)
        {
            builder.Append(w.Page).Append(' ').Append(w.Text).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
