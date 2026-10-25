using System.Text.Json;
using System.Text.Json.Serialization;

namespace TextIntegrityCheck;

/// <summary>
/// The fields of one runs.jsonl line that the checker reads. A layout harness
/// may write more fields; the checker ignores them. The checker never reads
/// measurements, console output, the page DOM or anything a page script computed.
/// </summary>
internal sealed class RunRecord
{
    public string RunId { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Markup { get; set; } = string.Empty;

    public int Repetition { get; set; } = 1;

    /// <summary>Declared mutation of a negative-control run (NC-W, NC-L, NC-R, NC-F), or null.</summary>
    public string? Mutation { get; set; }

    /// <summary>"none" | "expected".</summary>
    public string Marks { get; set; } = "none";

    /// <summary>"none" | "accounting" (whether a page script writes footer text).</summary>
    public string Footer { get; set; } = "none";

    /// <summary>ok | timeout | killed-wall-clock | oom-killed | crashed | error | not-run.</summary>
    public string Outcome { get; set; } = string.Empty;

    public string? Reason { get; set; }

    /// <summary>The PDF path relative to the runs folder, when the run wrote one.</summary>
    public string? Pdf { get; set; }

    public int? Dialogs { get; set; }

    public int? AbortedRequests { get; set; }

    public int? Scripts { get; set; }
}

/// <summary>One planned run of plan.json.</summary>
internal sealed class PlannedRun
{
    public string RunId { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Markup { get; set; } = string.Empty;

    public int Repetition { get; set; }

    public string? Mutation { get; set; }
}

internal sealed class Plan
{
    public string Matrix { get; set; } = string.Empty;

    public List<PlannedRun> Runs { get; set; } = [];
}

internal static class RunFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions JsonCompact = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Every record of runs.jsonl, in file order; a later line for the same run id replaces an earlier one.</summary>
    public static List<RunRecord> ReadRuns(string runsDirectory)
    {
        var path = Path.Combine(runsDirectory, "runs.jsonl");
        var records = new List<RunRecord>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<RunRecord>(line, Json) ?? throw new InvalidDataException("Empty run record.");
            if (index.TryGetValue(record.RunId, out var at))
            {
                records[at] = record;
            }
            else
            {
                index[record.RunId] = records.Count;
                records.Add(record);
            }
        }

        return records;
    }

    public static Plan ReadPlan(string runsDirectory) =>
        JsonSerializer.Deserialize<Plan>(File.ReadAllText(Path.Combine(runsDirectory, "plan.json")), Json) ?? throw new InvalidDataException("Empty plan.");
}
