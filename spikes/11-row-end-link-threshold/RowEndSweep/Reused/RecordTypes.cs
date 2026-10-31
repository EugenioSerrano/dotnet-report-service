using System.Text.Json;
using System.Text.Json.Serialization;
namespace TwoPassSpike;
/// <summary>
/// One line of runs.jsonl — the contract of the text-integrity checker in
/// spikes/07-native-tall-rows: one line per PDF it must read. The checker reads
/// only runId, fixtureId, markup, repetition, mutation, marks, footer, outcome,
/// reason, pdf and the escaping counters; the other fields are for people.
/// </summary>
internal sealed class RunRecord
{
    public required string RunId { get; set; }

    public required string FixtureId { get; set; }

    public required string Markup { get; set; }

    public int Repetition { get; set; } = 1;

    public string? Mutation { get; set; }

    public string Marks { get; set; } = "none";

    public string Footer { get; set; } = "none";

    /// <summary>ok | timeout | killed-wall-clock | oom-killed | crashed | error | not-run.</summary>
    public string Outcome { get; set; } = "error";

    public string? Reason { get; set; }

    public string? Pdf { get; set; }

    public int? Dialogs { get; set; }

    public int? AbortedRequests { get; set; }

    public int? Scripts { get; set; }

    public string? Run { get; set; }

    public int? Pass { get; set; }

    public string? Strategy { get; set; }

    public string? Fault { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// What one pass of one run did and measured. Pass 1 fills the layout fields,
/// pass 2 the read-back and stamp fields; the driver adds wall time, the child's
/// exit code, the oom_kill counter around the pass and the leftover processes.
/// </summary>
internal sealed class PassRecord
{
    /// <summary>ok | timeout | killed-wall-clock | oom-killed | crashed | error | guard-failed | not-run.</summary>
    public string Outcome { get; set; } = "error";

    public string? Reason { get; set; }

    public string? Pdf { get; set; }

    public long? PdfBytes { get; set; }

    public int? Pages { get; set; }

    public string? StartedAt { get; set; }

    public double? WallMs { get; set; }

    public double? TotalMs { get; set; }

    // Pass 1.
    public double? NavigationMs { get; set; }

    public double? FontsMs { get; set; }

    public double? PdfMs { get; set; }

    public int? Dialogs { get; set; }

    public int? AbortedRequests { get; set; }

    public int? Scripts { get; set; }

    public long? HtmlBytes { get; set; }

    public string? ChromiumVersion { get; set; }

    public string? PlaywrightVersion { get; set; }

    public List<string> ConsoleMessages { get; set; } = [];

    // Pass 2.
    public double? OpenMs { get; set; }

    public double? ReadMs { get; set; }

    public double? ComputeMs { get; set; }

    public double? StampMs { get; set; }

    public double? RemoveMs { get; set; }

    public double? SaveMs { get; set; }

    public int? AnnotationsRead { get; set; }

    public int? MarkerAnnotations { get; set; }

    public int? UnknownAnnotations { get; set; }

    public int? MarkersRemoved { get; set; }

    public string? Readback { get; set; }

    public string? Stamp { get; set; }

    public string? PdfSharpVersion { get; set; }

    // Both passes.
    public SampleSummary? Memory { get; set; }

    public long? HarnessVmHwmKiB { get; set; }

    public long? OomKillBefore { get; set; }

    public long? OomKillAfter { get; set; }

    public int? ChildExitCode { get; set; }

    public int? LeftoverProcessesKilled { get; set; }
}

/// <summary>One line of twopass.jsonl: one run, both passes.</summary>
internal sealed class TwoPassRecord
{
    public required string Run { get; set; }

    public required string FixtureId { get; set; }

    public required string Markup { get; set; }

    public required string Strategy { get; set; }

    public int Repetition { get; set; } = 1;

    /// <summary>A reused-oracle control mutation applied to the printed HTML (NC-W, NC-L, NC-R), or null.</summary>
    public string? Mutation { get; set; }

    /// <summary>A second-pass fault (AC-1 … AC-8, RB-1 … RB-3), or null.</summary>
    public string? Fault { get; set; }

    public int Group { get; set; }

    public string? Series { get; set; }

    public required PassRecord Pass1 { get; set; }

    public required PassRecord Pass2 { get; set; }
}
