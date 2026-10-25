using System.Text.Json;
using System.Text.Json.Serialization;

namespace PageAccountingCheck;

/// <summary>
/// The fields of one twopass.jsonl line the checker reads: what was run and how
/// each pass ended, the escaping counters of pass 1, the PDF and read-back paths.
/// Measurements, console output and the stamp record are never read.
/// </summary>
internal sealed class TwoPassLine
{
    public string Run { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Markup { get; set; } = string.Empty;

    public string Strategy { get; set; } = string.Empty;

    public int Repetition { get; set; } = 1;

    public string? Mutation { get; set; }

    public string? Fault { get; set; }

    public int Group { get; set; }

    public PassLine Pass1 { get; set; } = new();

    public PassLine Pass2 { get; set; } = new();
}

internal sealed class PassLine
{
    public string Outcome { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? Pdf { get; set; }

    public int? Dialogs { get; set; }

    public int? AbortedRequests { get; set; }

    public int? Scripts { get; set; }

    public string? Readback { get; set; }
}

internal sealed class PlanFile
{
    public string Matrix { get; set; } = string.Empty;

    public List<PlanEntry> Runs { get; set; } = [];

    /// <summary>The markups in which strategy "stamped" runs, after the marker probe's position check.</summary>
    public List<string> StampedMarkups { get; set; } = [];

    /// <summary>Planned "stamped" runs replaced by their "cell" twin (each also has a "not-run" run record).</summary>
    public List<ReplacedEntry> Replaced { get; set; } = [];
}

internal sealed class ReplacedEntry
{
    public string Run { get; set; } = string.Empty;

    public string ReplacedBy { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}

internal sealed class PlanEntry
{
    public string Run { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Markup { get; set; } = string.Empty;

    public string Strategy { get; set; } = string.Empty;

    public int Repetition { get; set; }

    public string? Mutation { get; set; }

    public string? Fault { get; set; }

    public int Group { get; set; }

    /// <summary>Set when the run is planned but not run (a markup where strategy "stamped" may not run).</summary>
    public string? NotRunReason { get; set; }
}

/// <summary>A rectangle in points, origin top-left, as the second pass wrote it.</summary>
internal sealed class RectLine
{
    public double X0 { get; set; }

    public double Y0 { get; set; }

    public double X1 { get; set; }

    public double Y1 { get; set; }

    /// <summary>Whether the point lies inside the rectangle grown by <paramref name="tolerance"/> on every side.</summary>
    public bool Contains(double x, double y, double tolerance) =>
        x >= X0 - tolerance && x <= X1 + tolerance && y >= Y0 - tolerance && y <= Y1 + tolerance;
}

internal sealed class ReadBackRowLine
{
    public int Seq { get; set; }

    public int StartPage { get; set; }

    public RectLine? StartRect { get; set; }

    public int EndPage { get; set; }

    public RectLine? EndRect { get; set; }

    public int EndRectPage { get; set; }

    public int? AmountMarkerPage { get; set; }
}

/// <summary>The second pass's own reading (readback/&lt;run&gt;.json), read only to be judged.</summary>
internal sealed class ReadBackFile
{
    public List<ReadBackRowLine> Rows { get; set; } = [];

    public int AnnotationsRead { get; set; }

    public int MarkerAnnotations { get; set; }

    public int UnknownAnnotations { get; set; }

    public int PagesRead { get; set; }
}

/// <summary>The fields of one record of the reused checker's integrity.json that this checker reads.</summary>
internal sealed class IntegrityEntry
{
    public string RunId { get; set; } = string.Empty;

    public string Verdict { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? Mutation { get; set; }

    public int? Pages { get; set; }

    public string? ContentSha256 { get; set; }

    public List<int>? PageWordCounts { get; set; }

    public int FailureCount { get; set; }

    public List<IntegrityFailure>? Failures { get; set; }

    public SortedDictionary<string, string>? Rules { get; set; }

    public IntegrityObservations? Observations { get; set; }

    public IntegrityControl? Control { get; set; }
}

internal sealed class IntegrityFailure
{
    public string Rule { get; set; } = string.Empty;

    public string Class { get; set; } = string.Empty;

    public int? Row { get; set; }

    public string? Region { get; set; }

    public string? Field { get; set; }

    public int? Page { get; set; }

    public string? Detail { get; set; }
}

internal sealed class IntegrityObservations
{
    public List<IntegritySpan> MultiPageRows { get; set; } = [];
}

internal sealed class IntegritySpan
{
    public int Row { get; set; }

    public int FirstPage { get; set; }

    public int LastPage { get; set; }

    public int? AmountPage { get; set; }
}

internal sealed class IntegrityControl
{
    public bool Behaved { get; set; }
}

internal sealed class IntegrityFileLine
{
    public List<IntegrityEntry> Runs { get; set; } = [];
}

internal static class Files
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

    public static PlanFile ReadPlan(string runsDirectory) =>
        JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(Path.Combine(runsDirectory, "plan.json")), Json) ?? throw new InvalidDataException("Empty plan.");

    /// <summary>Every twopass.jsonl record by run; a later line for the same run replaces an earlier one.</summary>
    public static Dictionary<string, TwoPassLine> ReadTwoPass(string runsDirectory)
    {
        var records = new Dictionary<string, TwoPassLine>(StringComparer.Ordinal);
        var path = Path.Combine(runsDirectory, "twopass.jsonl");
        if (!File.Exists(path))
        {
            return records;
        }

        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var record = JsonSerializer.Deserialize<TwoPassLine>(line, Json) ?? throw new InvalidDataException("Empty run record.");
            records[record.Run] = record;
        }

        return records;
    }

    /// <summary>The run ids of runs.jsonl (one per PDF the reused checker must read).</summary>
    public static HashSet<string> ReadContractRunIds(string runsDirectory)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(runsDirectory, "runs.jsonl");
        if (!File.Exists(path))
        {
            return ids;
        }

        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var document = JsonDocument.Parse(line);
            ids.Add(document.RootElement.GetProperty("runId").GetString() ?? string.Empty);
        }

        return ids;
    }

    public static Dictionary<string, IntegrityEntry> ReadIntegrity(string path)
    {
        var file = JsonSerializer.Deserialize<IntegrityFileLine>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty integrity file.");
        var result = new Dictionary<string, IntegrityEntry>(StringComparer.Ordinal);
        foreach (var entry in file.Runs)
        {
            result[entry.RunId] = entry;
        }

        return result;
    }

    public static ReadBackFile? ReadReadBack(string runsDirectory, string? relative)
    {
        if (relative is null)
        {
            return null;
        }

        var path = Path.Combine(runsDirectory, relative);
        return File.Exists(path) ? JsonSerializer.Deserialize<ReadBackFile>(File.ReadAllText(path), Json) : null;
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
