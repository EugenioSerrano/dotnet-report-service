using System.Globalization;
using System.Text.Json.Nodes;

namespace StrictAccountingCheck;

/// <summary>
/// The "compare" command: every grid run of this experiment beside the run of the
/// same id of the grid experiment of spikes/08-pagedjs-pre-split (its committed
/// summary.json, integrity.json and printed-accounting check), field by field.
/// Every difference is listed; memory and time are set beside, never compared.
/// </summary>
internal static class Compare
{
    public static async Task<int> RunAsync(string referenceSummaryPath, string referenceIntegrityPath, string accountingPath, string runsDirectory, string outPath, string? baseDigest)
    {
        var reference = JsonNode.Parse(File.ReadAllText(referenceSummaryPath))!;
        var referenceRuns = reference["runs"]!.AsArray().ToDictionary(r => r!["runId"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var referencePa = reference["printedAccounting"]!.AsArray().ToDictionary(r => r!["runId"]!.GetValue<string>(), r => r!["result"]!.GetValue<string>(), StringComparer.Ordinal);
        var referenceTi = JsonNode.Parse(File.ReadAllText(referenceIntegrityPath))!["runs"]!.AsArray()
            .ToDictionary(r => r!["runId"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var accounting = Controls.ReadAccounting(accountingPath);
        var stage = StageFolder.Load(runsDirectory);
        var rows = new List<JsonObject>();
        var differences = 0;
        foreach (var e in accounting.Where(e => e.Markup == "grid" && e.MatrixRow != 13))
        {
            var id = e.Run["s1-grid-".Length..];
            var run = stage.Runs.GetValueOrDefault(e.Run);
            var row = new JsonObject { ["run"] = e.Run, ["reference"] = id };
            var listed = new JsonArray();
            if (!referenceRuns.TryGetValue(id, out var r))
            {
                listed.Add("no run of that id in the reference");
            }
            else
            {
                void Field(string name, object? mine, object? theirs)
                {
                    var a = Convert.ToString(mine, CultureInfo.InvariantCulture);
                    var b = Convert.ToString(theirs, CultureInfo.InvariantCulture);
                    row[name] = new JsonObject { ["here"] = a, ["reference"] = b };
                    if (a != b)
                    {
                        listed.Add($"{name}: {a} here, {b} in the reference");
                    }
                }

                Field("class", e.RunClass, r["outcome"]?.GetValue<string>());
                Field("libraryPages", run?.Pages, r["pages"]?.GetValue<int?>());
                Field("parts", run?.Parts, r["splitResult"]?["partsTotal"]?.GetValue<int?>());
                Field("splitRows", run?.SplitRows, r["splitResult"]?["splitRows"]?.AsArray().Count);
                Field("libraryRules", run?.OwnRules, r["rules"] is JsonArray rules
                    ? string.Join(' ', rules.Select(x => $"{x!["name"]!.GetValue<string>()}:{(x["pass"]!.GetValue<bool>() ? "P" : "F")}"))
                    : null);
                Field("layoutRepeated", e.LayoutRepeated, r["layoutRepeatedError"]?.GetValue<bool>() == true || r["layoutRepeatedAnyLevel"]?.GetValue<bool>() == true);
                Field("semInnerSha256", e.SemInnerSha256, r["sem"]?["pdfInnerSha256"]?.GetValue<string>());
                Field("htmlBytes", e.HtmlBytes, r["htmlBytes"]?.GetValue<long?>());
                var ti = referenceTi.GetValueOrDefault(id);
                Field("textVerdict", e.TextVerdict, ti?["verdict"]?.GetValue<string>());
                Field("contentSha256", e.ContentSha256, ti?["contentSha256"]?.GetValue<string>());
                Field("pdfPages", e.Pages, ti?["pages"]?.GetValue<int?>());
                row["beside"] = new JsonObject
                {
                    ["chromiumTreePeakRssKiB"] = new JsonObject { ["here"] = run?.ChromiumTreePeakRssKiB, ["reference"] = r["memory"]?["chromiumTreePeakRssKiB"]?.GetValue<long?>() },
                    ["layoutMs"] = new JsonObject { ["here"] = run?.LayoutMs, ["reference"] = r["layoutMs"]?.GetValue<double?>() },
                    ["wallMs"] = new JsonObject { ["here"] = run?.WallMs, ["reference"] = r["wallMs"]?.GetValue<double?>() },
                    ["printedAccountingPrefixForm"] = referencePa.GetValueOrDefault(id),
                    ["strictAccounting"] = e.AccountingVerdict,
                };
            }

            row["differences"] = listed;
            differences += listed.Count;
            rows.Add(row);
        }

        var document = new JsonObject
        {
            ["baseDigestHere"] = baseDigest,
            ["baseDigestReference"] = "sha256:20a7c39a219fe9ad039a8cda5d31cbc0c3e1d1166b729cc1c719b4ccd09de60d",
            ["runsCompared"] = rows.Count,
            ["runsWithDifferences"] = rows.Count(r => r["differences"]!.AsArray().Count > 0),
            ["differences"] = differences,
            ["runs"] = new JsonArray([.. rows]),
        };
        await File.WriteAllTextAsync(outPath, document.ToJsonString(StageFolder.Json) + "\n");
        Console.WriteLine($"{rows.Count} grid runs compared with the grid experiment: {document["runsWithDifferences"]} with differences ({differences} in all).");
        foreach (var row in rows.Where(r => r["differences"]!.AsArray().Count > 0))
        {
            Console.WriteLine($"  {row["run"]}: {string.Join("; ", row["differences"]!.AsArray().Select(d => d!.GetValue<string>()))}");
        }

        return 0;
    }
}
