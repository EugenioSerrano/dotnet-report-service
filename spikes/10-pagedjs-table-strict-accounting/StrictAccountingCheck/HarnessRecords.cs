using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StrictAccountingCheck;

/// <summary>
/// The fields of one runs.jsonl line of the layout harness that this checker reads.
/// For a verdict it reads only the run's identity, its outcome class, its PDF and
/// its escaping counters; the measurements (times, memory, split result) and the
/// layout library's own result (its rule lines and console signals) are copied to
/// the results to be reported beside, never used for a verdict.
/// </summary>
internal sealed class HarnessRun
{
    public string RunId { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Markup { get; set; } = string.Empty;

    public int Repetition { get; set; } = 1;

    public string? Mutation { get; set; }

    public string Marks { get; set; } = "none";

    public string Footer { get; set; } = "none";

    public string Outcome { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? Pdf { get; set; }

    public int? Dialogs { get; set; }

    public int? AbortedRequests { get; set; }

    public int? NotFoundRequests { get; set; }

    public int? Scripts { get; set; }

    public int Stage { get; set; }

    public int MatrixRow { get; set; }

    public string Split { get; set; } = "off";

    public double? Trigger { get; set; }

    public double? PartLimit { get; set; }

    public double? WallMs { get; set; }

    public double? MeasureMs { get; set; }

    public double? SplitMs { get; set; }

    public double? LayoutMs { get; set; }

    public double? PagedFlowMs { get; set; }

    public double? PdfMs { get; set; }

    public double? TotalMs { get; set; }

    public int? Pages { get; set; }

    public long? PdfBytes { get; set; }

    public long? HtmlBytes { get; set; }

    public JsonNode? Memory { get; set; }

    public long? HarnessVmHwmKiB { get; set; }

    public long? CgroupMemoryPeakBytes { get; set; }

    public bool? LayoutRepeatedError { get; set; }

    public bool? LayoutRepeatedAnyLevel { get; set; }

    public int ConsoleTotal { get; set; }

    public JsonNode? Rules { get; set; }

    public JsonNode? SplitResult { get; set; }

    public JsonNode? Sem { get; set; }

    [JsonIgnore]
    public bool FooterOn => Footer == "accounting";

    [JsonIgnore]
    public bool SemPass => Sem?["pass"]?.GetValue<bool>() == true
        || (Sem?["a"]?.GetValue<bool>() == true && Sem?["b"]?.GetValue<bool>() == true && Sem?["c"]?.GetValue<bool>() == true);

    [JsonIgnore]
    public string? SemInnerSha256 => Sem?["pdfInnerSha256"]?.GetValue<string>();

    [JsonIgnore]
    public bool LayoutRepeated => LayoutRepeatedError == true || LayoutRepeatedAnyLevel == true;

    [JsonIgnore]
    public long? ChromiumTreePeakRssKiB => Memory?["chromiumTreePeakRssKiB"]?.GetValue<long>();

    [JsonIgnore]
    public long? ChromiumTreePeakPssKiB => Memory?["chromiumTreePeakPssKiB"]?.GetValue<long>();

    [JsonIgnore]
    public int? Parts => SplitResult?["partsTotal"]?.GetValue<int>();

    [JsonIgnore]
    public int? SplitRows => SplitResult?["splitRows"]?.AsArray().Count;

    /// <summary>The layout library's own rule lines (name: PASS or FAIL), as the harness recorded them.</summary>
    [JsonIgnore]
    public string OwnRules => Rules is JsonArray rules
        ? string.Join(' ', rules.Select(r => $"{r!["name"]!.GetValue<string>()}:{(r["pass"]!.GetValue<bool>() ? "P" : "F")}"))
        : string.Empty;
}

/// <summary>One planned run of a stage's plan.json.</summary>
internal sealed class HarnessPlanned
{
    public string RunId { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Markup { get; set; } = string.Empty;

    public int Repetition { get; set; }

    public string? Mutation { get; set; }

    public string Split { get; set; } = string.Empty;

    public double? Trigger { get; set; }

    public double? PartLimit { get; set; }

    public bool FooterOn { get; set; }

    public int Stage { get; set; }

    public int MatrixRow { get; set; }
}

internal sealed class HarnessPlan
{
    public string Matrix { get; set; } = string.Empty;

    public List<HarnessPlanned> Runs { get; set; } = [];
}

/// <summary>A stage folder: its plan and its run records (a later line for the same run replaces an earlier one).</summary>
internal sealed class StageFolder
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required string Directory { get; init; }

    public required HarnessPlan Plan { get; init; }

    public required Dictionary<string, HarnessRun> Runs { get; init; }

    /// <summary>Lines of runs.jsonl, counted per run id (a resumed stage never writes a run twice).</summary>
    public required Dictionary<string, int> RecordCounts { get; init; }

    public static StageFolder Load(string directory)
    {
        var plan = JsonSerializer.Deserialize<HarnessPlan>(File.ReadAllText(Path.Combine(directory, "plan.json")), Json)
            ?? throw new InvalidDataException("Empty plan.");
        var runs = new Dictionary<string, HarnessRun>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = Path.Combine(directory, "runs.jsonl");
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var run = JsonSerializer.Deserialize<HarnessRun>(line, Json) ?? throw new InvalidDataException("Empty run record.");
                runs[run.RunId] = run;
                counts[run.RunId] = counts.GetValueOrDefault(run.RunId) + 1;
            }
        }

        return new StageFolder { Directory = directory, Plan = plan, Runs = runs, RecordCounts = counts };
    }

    public static async Task WriteJsonAsync<T>(string path, T value)
    {
        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, value, Json);
        }

        await File.AppendAllTextAsync(path, "\n");
    }
}
