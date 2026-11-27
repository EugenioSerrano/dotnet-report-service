namespace ReportService.TemplateChecks.Sample;

/// <summary>
/// What a render that finished produced: the pages, bytes and words of the printed
/// document and the three timings the take's figures read. The figures are the
/// sample's own and carry no value of the data.
/// </summary>
/// <param name="Pages">How many pages the printed sample holds.</param>
/// <param name="HtmlBytes">The size of the composed HTML the sample was laid out from.</param>
/// <param name="PdfBytes">The size of the printed document.</param>
/// <param name="Words">How many words the printed document holds.</param>
/// <param name="ComposeMs">How long composing the sample took, both compositions included.</param>
/// <param name="PrintMs">How long printing the sample took.</param>
/// <param name="VerifyMs">How long verifying the printed sample took.</param>
/// <param name="HtmlSha256">The SHA-256 of the composed HTML, lower-case hexadecimal: the demo's identity of the three engines' report.</param>
/// <param name="CodePages">How many pages carried the declared per-page code, when the package declares one.</param>
public sealed record SampleFacts(
    int Pages,
    long HtmlBytes,
    long PdfBytes,
    int Words,
    double ComposeMs,
    double PrintMs,
    double VerifyMs,
    string? HtmlSha256 = null,
    int? CodePages = null);

/// <summary>The deterministic properties of a version that a sample render refuses it for.</summary>
public enum SampleFailureKind
{
    /// <summary>The engine raised while rendering a part that parsed.</summary>
    EngineError,

    /// <summary>The engine's own limit was reached on this template's data.</summary>
    EngineLimit,

    /// <summary>The caller-side output guard stopped one render call or the block.</summary>
    GuardOutput,

    /// <summary>The printed page does not carry what the parts composed.</summary>
    Verification,

    /// <summary>The sample cannot print a report with no records.</summary>
    EmptySample,

    /// <summary>The sample is larger than one block; decided before anything is composed.</summary>
    OverBound,

    /// <summary>The sample does not follow the data format the delivered reader reads.</summary>
    Unreadable,

    /// <summary>
    /// The printed sample cannot carry the declared per-page code: a page prints text
    /// inside the code's area, a page's source yields no value, the generator refuses
    /// a value or returns content that is not allowed, or the code does not verify.
    /// </summary>
    PageCode,
}

/// <summary>
/// One deterministic reason a sample does not render: the class, the part it is
/// attributed to, and the diagnostic position the author needs - a field path, a
/// limit, a block index, a record ordinal or the sample's own counts. It never
/// carries a value, a printed text, a storage key or the engine's own message.
/// </summary>
/// <param name="Kind">The class of the failure.</param>
/// <param name="Part">The package-relative file the failure is attributed to, when one is known.</param>
/// <param name="AnchorPath">The field path an anchor failure names, when one applies.</param>
/// <param name="Limit">The engine's own limit, when one was reached.</param>
/// <param name="BlockIndex">The block the failure was found in, when one applies.</param>
/// <param name="RecordOrdinal">The record the failure was found at, when one applies.</param>
/// <param name="Reason">The verification's own reason word, when the failure is one of its verdicts.</param>
/// <param name="Records">How many records an over-bound sample holds.</param>
/// <param name="RecordBytes">How many bytes of record content an over-bound sample holds.</param>
/// <param name="Bound">The bound an over-bound sample crossed.</param>
/// <param name="PageNumber">The printed page the failure was found on, when one applies.</param>
public sealed record SampleFailure(
    SampleFailureKind Kind,
    string? Part = null,
    string? AnchorPath = null,
    string? Limit = null,
    int? BlockIndex = null,
    long? RecordOrdinal = null,
    string? Reason = null,
    int? Records = null,
    long? RecordBytes = null,
    ReportService.Templates.SampleBound? Bound = null,
    int? PageNumber = null);

/// <summary>Why a render could not complete, and so why nothing is said about the version.</summary>
public enum SampleUnavailableCause
{
    /// <summary>A composition or a print passed its deadline; the code is <c>RENDER_TIMEOUT</c>.</summary>
    RenderTimeout,

    /// <summary>The browser crashed or the process lost it; no code of its own.</summary>
    RenderCrash,
}

/// <summary>
/// What one sample render answered: the facts of a render that finished, the
/// deterministic failure of one that refused the version, or the cause of one that
/// could not run at all. The third state is never a refusal: the take retries it
/// (<c>RENDER_TIMEOUT</c> or <c>EXECUTION_FAILED</c> when the attempts exhaust).
/// </summary>
public abstract record SampleAttempt
{
    /// <summary>The render finished and every check passed.</summary>
    /// <param name="Facts">The figures the take records.</param>
    public sealed record Rendered(SampleFacts Facts) : SampleAttempt;

    /// <summary>The render finished and the version is refused for a deterministic property of it.</summary>
    /// <param name="Failure">The class, the part and the diagnostic position.</param>
    public sealed record Failed(SampleFailure Failure) : SampleAttempt;

    /// <summary>The render did not complete; nothing is said about the version.</summary>
    /// <param name="Cause">A deadline trip or a crash.</param>
    /// <param name="Part">The part that was rendering, when it is known.</param>
    public sealed record Unavailable(SampleUnavailableCause Cause, string? Part = null) : SampleAttempt;
}
