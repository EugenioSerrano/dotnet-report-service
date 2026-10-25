using System.Text.Json;
using System.Text.Json.Nodes;
using PdfLayoutSpike;

namespace PreSplitLayoutSpike;

/// <summary>
/// Reads what one stage left behind — its runs.jsonl and the checker's
/// integrity file — and answers the questions the selection and the verdict
/// ask of a run: its rules, its text-integrity verdict and its console signals.
/// </summary>
internal sealed class Evidence
{
    /// <summary>Rules 1-5 of the end-page rule (rule 5 includes the split-attribute diagnostic when present).</summary>
    public static readonly string[] Rules1To5 =
    [
        AccountingVerifier.Rows, AccountingVerifier.EndPageSubtotals, AccountingVerifier.Chain, AccountingVerifier.VisibleAmounts, AccountingVerifier.Marks,
    ];

    public Evidence(IEnumerable<RunRecord> records, IEnumerable<JsonObject> integrity)
    {
        Records = [.. records];
        Integrity = integrity.ToDictionary(r => r["runId"]!.GetValue<string>(), r => r, StringComparer.Ordinal);
    }

    public List<RunRecord> Records { get; }

    public Dictionary<string, JsonObject> Integrity { get; }

    public static List<RunRecord> ReadRuns(string runsDirectory)
    {
        var path = Path.Combine(runsDirectory, "runs.jsonl");
        var byId = new Dictionary<string, RunRecord>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var record = JsonSerializer.Deserialize<RunRecord>(line, RunRecord.Json)!;
            if (!byId.ContainsKey(record.RunId))
            {
                order.Add(record.RunId);
            }

            byId[record.RunId] = record;
        }

        return [.. order.Select(id => byId[id])];
    }

    /// <summary>
    /// Loads the records and integrity entries of one or more stages; the i-th integrity file belongs to the i-th runs
    /// folder. When more than one stage is merged, every run id is qualified with its stage ("s1-…", "s2-…"): the
    /// same configuration can be planned in both stages (the selected values may equal the stage-1 defaults), and each
    /// run keeps its stage.
    /// </summary>
    public static (List<RunRecord> Records, List<JsonObject> Integrity, Dictionary<string, string> DirectoryOf, List<(string Directory, int Stage)> Stages) LoadStages(
        IReadOnlyList<string> runsDirectories, IReadOnlyList<string> integrityPaths)
    {
        if (runsDirectories.Count != integrityPaths.Count)
        {
            throw new ArgumentException("One integrity file is needed per runs folder, in the same order.");
        }

        var qualify = runsDirectories.Count > 1;
        var records = new List<RunRecord>();
        var integrity = new List<JsonObject>();
        var directoryOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var stages = new List<(string, int)>();
        for (var i = 0; i < runsDirectories.Count; i++)
        {
            var stageRecords = ReadRuns(runsDirectories[i]);
            var stage = stageRecords.FirstOrDefault()?.Stage ?? 0;
            stages.Add((runsDirectories[i], stage));
            foreach (var record in stageRecords)
            {
                record.RunId = qualify ? Qualified(stage, record.RunId) : record.RunId;
                records.Add(record);
                directoryOf[record.RunId] = runsDirectories[i];
            }

            foreach (var entry in ReadIntegrity(integrityPaths[i]))
            {
                if (qualify)
                {
                    entry["runId"] = Qualified(stage, entry["runId"]!.GetValue<string>());
                }

                integrity.Add(entry);
            }
        }

        return (records, integrity, directoryOf, stages);
    }

    public static string Qualified(int stage, string runId) => $"s{stage}-{runId}";

    /// <summary>The run id as the stage's own files name it (without the stage qualifier).</summary>
    public static string Unqualified(string runId) =>
        runId.Length > 3 && runId[0] == 's' && char.IsAsciiDigit(runId[1]) && runId[2] == '-' ? runId[3..] : runId;

    public static List<JsonObject> ReadIntegrity(string path) =>
        [.. JsonNode.Parse(File.ReadAllText(path))!["runs"]!.AsArray().Select(n => n!.AsObject())];

    public JsonObject? IntegrityOf(RunRecord run) => Integrity.GetValueOrDefault(run.RunId);

    /// <summary>pass | fail | no-pdf | no-check | missing.</summary>
    public string TiVerdict(RunRecord run) => IntegrityOf(run)?["verdict"]?.GetValue<string>() ?? "missing";

    public static bool RulePasses(RunRecord run, string name) => run.Rules.Any(r => r.Name == name && r.Pass);

    public static bool SplitAttributesPass(RunRecord run) => run.Rules.Where(r => r.Name == AccountingVerifier.SplitAttributes).All(r => r.Pass);

    /// <summary>Rules 1-5 present and passing (footer-on runs).</summary>
    public static bool Rules1To5Pass(RunRecord run) => run.Outcome == "ok" && Rules1To5.All(n => RulePasses(run, n)) && SplitAttributesPass(run);

    /// <summary>Rules 1-6 and 8 present and passing; rule 7 is a property of an on/off pair and is checked apart.</summary>
    public static bool RulesPerRunPass(RunRecord run) =>
        Rules1To5Pass(run) && RulePasses(run, AccountingVerifier.Geometry) && RulePasses(run, AccountingVerifier.Escaping);

    public static bool NoLayoutRepeated(RunRecord run) => run.LayoutRepeatedError != true && run.LayoutRepeatedAnyLevel != true;

    public static string RuleLine(RunRecord run) => string.Join("; ", run.Rules.Select(r => $"{r.Name} {r.Outcome}" + (r.FirstFailingPage is null ? string.Empty : $" p.{r.FirstFailingPage}") + (r.Detail is null ? string.Empty : $" ({r.Detail})")));

    public static string Signals(RunRecord run) => $"Layout repeated: error-level {(run.LayoutRepeatedError == true ? "yes" : "no")}, any level {(run.LayoutRepeatedAnyLevel == true ? "yes" : "no")}";

    /// <summary>The measured height fraction of row 50 from the pre-split script's measurement.</summary>
    public static double? Row50Fraction(RunRecord run) =>
        run.SplitResult?["measured"]?.AsArray().FirstOrDefault(m => m?["seq"]?.GetValue<int>() == 50)?["fraction"]?.GetValue<double>();

    public static int SplitRows(RunRecord run) => run.SplitResult?["splitRows"]?.AsArray().Count ?? 0;
}
