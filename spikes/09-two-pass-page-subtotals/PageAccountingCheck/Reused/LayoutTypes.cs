namespace PdfLayoutSpike;

/// <summary>What the in-page script reports once the layout is finished.</summary>
internal sealed class LayoutResult
{
    public int? Total { get; set; }

    public double? FlowMs { get; set; }

    public bool? FooterOn { get; set; }

    public bool? MarginBoxesFound { get; set; }

    public List<LayoutPage> Pages { get; set; } = [];

    public List<PageDiagnostic> Diagnostics { get; set; } = [];

    public int Scripts { get; set; }

    public string? AccountingError { get; set; }

    public string? Error { get; set; }
}

internal sealed class LayoutPage
{
    public int Page { get; set; }

    public List<LayoutFragment> Fragments { get; set; } = [];

    public FooterTexts Footer { get; set; } = new();

    public int Marks { get; set; }

    public double? AreaBottom { get; set; }

    public double? AreaTop { get; set; }

    /// <summary>Height of the page content area, in CSS pixels.</summary>
    public double? AreaHeight { get; set; }

    public List<int> BelowArea { get; set; } = [];

    public List<string> FooterOverflow { get; set; } = [];
}

internal sealed class LayoutFragment
{
    public int Seq { get; set; }

    public int Fragment { get; set; }

    public int Fragments { get; set; }

    public bool SplitFrom { get; set; }

    public bool SplitTo { get; set; }

    public string AmountBefore { get; set; } = string.Empty;

    public bool Mark { get; set; }

    public bool? Aligned { get; set; }

    /// <summary>Characters of the description shown in this fragment.</summary>
    public int DescChars { get; set; }

    public double? Top { get; set; }

    public double? Bottom { get; set; }
}

internal sealed class FooterTexts
{
    public string? Left { get; set; }

    public string? Center { get; set; }

    public string? Right { get; set; }
}

internal sealed class PageDiagnostic
{
    public int Page { get; set; }

    public int FragmentCount { get; set; }

    public bool FirstSplitFrom { get; set; }

    public bool LastSplitToAtLayout { get; set; }

    public bool HasBreakToken { get; set; }

    public bool? PreviousLastSplitToKnown { get; set; }
}

/// <summary>One laid-out and printed block, with its measurements.</summary>
internal sealed class RunResult
{
    public required string Id { get; init; }

    public required string Configuration { get; init; }

    public required string Block { get; init; }

    public required bool Footer { get; init; }

    public required int Repetition { get; init; }

    public required string Purpose { get; init; }

    public string Status { get; set; } = "ok";

    public string? Message { get; set; }

    public int? Pages { get; set; }

    public int? PdfPagesHeuristic { get; set; }

    public double NavigationMs { get; set; }

    public double? LayoutMs { get; set; }

    public double? PagedFlowMs { get; set; }

    public double? PdfMs { get; set; }

    public long? PdfBytes { get; set; }

    public int AbortedRequests { get; set; }

    public int NotFoundRequests { get; set; }

    public int Dialogs { get; set; }

    public int ScriptsExpected { get; set; }

    public int? ScriptsFound { get; set; }

    public SampleSummary? Memory { get; set; }

    public long? HarnessHighWaterMarkKiB { get; set; }

    public long? CgroupMemoryPeakBytes { get; set; }

    public List<CheckResult> Checks { get; set; } = [];

    public List<string> PageImages { get; set; } = [];

    /// <summary>Console warnings and errors of the page (at most 20).</summary>
    public List<string> ConsoleMessages { get; set; } = [];

    public string? PdfFile { get; set; }

    /// <summary>
    /// Outcome class of a characterisation run: "signature" or "different" for the
    /// giant-row fixture, "pass", "signature" or "other" for a tall-row fixture.
    /// </summary>
    public string? OutcomeClass { get; set; }

    /// <summary>Tall-row fixtures: the target multiple of the content-area height.</summary>
    public double? TargetK { get; set; }

    /// <summary>Tall-row fixtures: the actual length of the tall row's description.</summary>
    public int? TallRowLength { get; set; }

    /// <summary>Tall-row fixtures: the number of pages the tall row's fragments occupy.</summary>
    public int? ObservedSpan { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public LayoutResult? Layout { get; set; }
}
