using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PdfLayoutSpike;

namespace PreSplitLayoutSpike;

/// <summary>
/// One line of runs.jsonl: what was run, how it ended and what it measured.
/// The text-integrity checker of spikes/07-native-tall-rows reads only runId,
/// fixtureId, markup, repetition, mutation, marks, footer, outcome, reason, pdf
/// and the escaping counters (dialogs, abortedRequests, scripts); everything
/// else is this harness's own measurement.
/// </summary>
internal sealed class RunRecord
{
    public required string RunId { get; set; }

    public required string FixtureId { get; set; }

    /// <summary>Always "grid": the only markup of this experiment.</summary>
    public string Markup { get; set; } = "grid";

    public int Repetition { get; set; } = 1;

    public string? Mutation { get; set; }

    /// <summary>"expected" when the footer is on (the accounting script writes the marks), else "none".</summary>
    public string Marks { get; set; } = "none";

    /// <summary>"accounting" when the footer is on, else "none".</summary>
    public string Footer { get; set; } = "none";

    /// <summary>ok | timeout | killed-wall-clock | oom-killed | crashed | error | not-run.</summary>
    public string Outcome { get; set; } = "error";

    public string? Reason { get; set; }

    /// <summary>The PDF path relative to the runs folder, when the run wrote one.</summary>
    public string? Pdf { get; set; }

    public int? Dialogs { get; set; }

    public int? AbortedRequests { get; set; }

    /// <summary>
    /// Script elements in the laid-out page other than the four this harness wrote
    /// (the escaping counter the checker reads: 0 when nothing was injected).
    /// </summary>
    public int? Scripts { get; set; }

    public int Stage { get; set; }

    public int MatrixRow { get; set; }

    public string? Series { get; set; }

    public int SeriesIndex { get; set; }

    /// <summary>Every matrix row this run serves (the shared overhead and row-series run serves two).</summary>
    public List<int> MatrixRows { get; set; } = [];

    /// <summary>off | measure-only | split.</summary>
    public string Split { get; set; } = "off";

    public double? Trigger { get; set; }

    public double? PartLimit { get; set; }

    public string? StartedAt { get; set; }

    public double? WallMs { get; set; }

    public double? NavigationMs { get; set; }

    public double? FontsMs { get; set; }

    public double? MeasureMs { get; set; }

    public double? SplitMs { get; set; }

    /// <summary>From the start of the pagination library to the layout-finished signal (layout and accounting).</summary>
    public double? LayoutMs { get; set; }

    /// <summary>The pagination library's own flow time.</summary>
    public double? PagedFlowMs { get; set; }

    public double? PdfMs { get; set; }

    public double? TotalMs { get; set; }

    public int? Pages { get; set; }

    public int? PdfPagesHeuristic { get; set; }

    public long? PdfBytes { get; set; }

    public long? HtmlBytes { get; set; }

    public int? ScriptsFound { get; set; }

    public int? ScriptsExpected { get; set; }

    public int? NotFoundRequests { get; set; }

    public SampleSummary? Memory { get; set; }

    public long? HarnessVmHwmKiB { get; set; }

    public long? CgroupMemoryPeakBytes { get; set; }

    public long? OomKillBefore { get; set; }

    public long? OomKillAfter { get; set; }

    public int? ChildExitCode { get; set; }

    public int? LeftoverProcessesKilled { get; set; }

    public string? ChromiumVersion { get; set; }

    public string? PlaywrightVersion { get; set; }

    /// <summary>Every console message and page error, all levels, at most 200 per run.</summary>
    public List<ConsoleEntry> Console { get; set; } = [];

    public int ConsoleTotal { get; set; }

    public int ConsoleDropped { get; set; }

    /// <summary>The verifier's own signal: a console message of level error containing "Layout repeated".</summary>
    public bool? LayoutRepeatedError { get; set; }

    /// <summary>Any console message or page error, at any level, containing "Layout repeated".</summary>
    public bool? LayoutRepeatedAnyLevel { get; set; }

    public List<ConsoleEntry> LayoutRepeatedOccurrences { get; set; } = [];

    /// <summary>Rules 1-9 of spikes/02-pdf-layout as printed by its verifier (rule 7 is evaluated on on/off pairs).</summary>
    public List<CheckResult> Rules { get; set; } = [];

    public SignatureEvaluation? Signature { get; set; }

    /// <summary>The pre-split script's result (window.__splitResult).</summary>
    public JsonNode? SplitResult { get; set; }

    public SemResult? Sem { get; set; }

    /// <summary>The layout result file of this run, relative to the runs folder.</summary>
    public string? LayoutFile { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };
}

/// <summary>One console message or page error.</summary>
internal sealed record ConsoleEntry(string Level, string Text);

/// <summary>The semantic-source check of one run.</summary>
internal sealed class SemResult
{
    public string? SemanticSha256Before { get; set; }

    public string? SemanticSha256After { get; set; }

    /// <summary>SHA-256 of the body's inner content of semantic.html.</summary>
    public string? SemanticInnerSha256 { get; set; }

    /// <summary>SHA-256 of the body's inner content of pdf.html as served.</summary>
    public string? PdfInnerSha256 { get; set; }

    public string? SemanticBodyTag { get; set; }

    public string? PdfBodyTag { get; set; }

    public int? RowsExpected { get; set; }

    public int? RowsFound { get; set; }

    public int? RowsUnsplit { get; set; }

    public int? TextsEqual { get; set; }

    public int? ScriptsInSemanticPage { get; set; }

    public bool? A { get; set; }

    public bool? B { get; set; }

    public bool? C { get; set; }

    public bool Pass => A == true && B == true && C == true;

    public string? Detail { get; set; }
}
