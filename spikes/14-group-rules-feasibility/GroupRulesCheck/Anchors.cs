using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrictAccountingCheck;

namespace GroupRulesCheck;

/// <summary>
/// The "anchors" command: every run without group behaviours that
/// spikes/12-group-continuity also ran, beside that run (its committed
/// accounting.json and integrity.json), field by field — class, pages, HTML length,
/// body hash, content hash, row-set hash, the layout library's rule lines and its
/// repeated-layout signal, and the text verdict. Every difference is listed. That
/// folder's times and memory are set beside, never compared.
///
/// The committed accounting.json is that experiment's compact projection: its runs
/// are read by field name, not as this experiment's full entry type.
/// </summary>
internal static class AnchorsCommand
{
    public static async Task<int> RunAsync(string referenceAccountingPath, string referenceIntegrityPath, string evaluationPath, string outPath)
    {
        var reference = JsonNode.Parse(File.ReadAllText(referenceAccountingPath))!["runs"]!.AsArray()
            .ToDictionary(r => r!["run"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var referenceTi = JsonNode.Parse(File.ReadAllText(referenceIntegrityPath))!["runs"]!.AsArray()
            .ToDictionary(r => r!["runId"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var evaluation = EvaluateCommand.Read(evaluationPath);
        var rows = new JsonArray();
        var differences = 0;
        var compared = 0;
        foreach (var e in evaluation.Where(e => e.Anchor is not null))
        {
            var mine = e.Accounting;
            var row = new JsonObject { ["run"] = e.Run, ["reference"] = e.Anchor };
            var listed = new JsonArray();
            if (!reference.TryGetValue(e.Anchor!, out var r))
            {
                listed.Add("no run of that id in the reference");
            }
            else
            {
                void Field(string name, object? here, object? there)
                {
                    var a = Convert.ToString(here, CultureInfo.InvariantCulture);
                    var b = Convert.ToString(there, CultureInfo.InvariantCulture);
                    row[name] = new JsonObject { ["here"] = a, ["reference"] = b };
                    if (a != b)
                    {
                        listed.Add($"{name}: {a} here, {b} in the reference");
                    }
                }

                Field("class", mine.RunClass, r["runClass"]?.GetValue<string>());
                Field("pages", mine.Pages, r["pages"]?.GetValue<int>());
                Field("htmlBytes", mine.HtmlBytes, r["htmlBytes"]?.GetValue<long>());
                Field("semInnerSha256", mine.SemInnerSha256, r["semInnerSha256"]?.GetValue<string>());
                Field("contentSha256", mine.ContentSha256, r["contentSha256"]?.GetValue<string>());
                Field("rowSetsSha256", mine.RowSetsSha256, r["rowSetsSha256"]?.GetValue<string>());
                Field("libraryRules", mine.LibraryRules, r["libraryRules"]?.GetValue<string>());
                Field("layoutRepeated", mine.LayoutRepeated, r["layoutRepeated"]?.GetValue<bool>());
                var ti = referenceTi.GetValueOrDefault(e.Anchor!);
                Field("textVerdict", e.Text?.Verdict, ti?["verdict"]?.GetValue<string>());
                row["referenceMeasurements"] = new JsonObject
                {
                    ["layoutMs"] = r["measurements"]?["layoutMs"]?.DeepClone(),
                    ["pagedFlowMs"] = r["measurements"]?["pagedFlowMs"]?.DeepClone(),
                    ["totalMs"] = r["measurements"]?["totalMs"]?.DeepClone(),
                    ["chromiumTreePeakRssKiB"] = r["measurements"]?["chromiumTreePeakRssKiB"]?.DeepClone(),
                };
            }

            compared++;
            differences += listed.Count;
            row["differences"] = listed;
            rows.Add(row);
        }

        var document = new JsonObject { ["runsCompared"] = compared, ["differences"] = differences, ["runs"] = rows };
        await File.WriteAllTextAsync(outPath, document.ToJsonString(StageFolder.Json) + "\n");
        Console.WriteLine($"{compared} runs compared with spikes/12-group-continuity: {differences} differences.");
        foreach (var row in rows.Where(x => x!["differences"]!.AsArray().Count > 0))
        {
            Console.WriteLine($"  {row!["run"]}: {string.Join("; ", row["differences"]!.AsArray().Select(d => d!.GetValue<string>()))}");
        }

        return 0;
    }
}
