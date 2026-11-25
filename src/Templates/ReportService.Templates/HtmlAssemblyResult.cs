namespace ReportService.Templates;

/// <summary>The two outcomes of assembling the HTML output (ADR-046 §3.3).</summary>
public enum HtmlAssemblyOutcome
{
    /// <summary>One self-contained <c>.html</c> was written to the caller's stream.</summary>
    Produced,

    /// <summary>Nothing complete was written; a reference could not be inlined from the package.</summary>
    Refused,
}

/// <summary>
/// The coarse cause of a refusal, enough for the caller to pick the stable error
/// code without re-scanning (ADR-046 §3.3 item 5; the code names are deferred to
/// the error-catalogue owner, SPEC §14 D3).
/// </summary>
public enum HtmlRefusalCause
{
    /// <summary>The reference reaches outside the packaged template (remote, absolute or host).</summary>
    OutsidePackage,

    /// <summary>The reference is not on the closed inlinable list, or the packaged asset is absent.</summary>
    NonInlinableOrOffList,

    /// <summary>A resource-loading reference appears inside a repeated row or group region.</summary>
    RepeatedRegion,

    /// <summary>The reference resolves, after normalisation, outside the package root.</summary>
    TraversalEscape,

    /// <summary>An <c>@import</c> chain forms a cycle or exceeds the depth bound.</summary>
    ImportCycleOrDepth,

    /// <summary>
    /// The <c>documentStart</c> structure admits no conforming <c>&lt;head&gt;</c> carrying the
    /// Content-Security-Policy <c>&lt;meta&gt;</c> as its first child: a rendered body element
    /// precedes an existing <c>&lt;head&gt;</c>, more than one <c>&lt;head&gt;</c> is present, or
    /// no head/html/content anchor is found within the bounded head scan (ADR-047 §3.8 item 3).
    /// </summary>
    HeadNotConformable,
}

/// <summary>The first offending reference of a refused assembly: its coarse cause, the reference and its region.</summary>
/// <param name="Cause">The coarse refusal cause.</param>
/// <param name="Reference">The offending reference, as the template wrote it.</param>
/// <param name="Region">Where it appeared (document start/end, a stylesheet, a repeated region).</param>
public sealed record HtmlRefusal(HtmlRefusalCause Cause, string Reference, string Region);

/// <summary>
/// The result of assembling the HTML output: produced, or refused with a reason.
/// <para>
/// <b>Refused ⇒ discard (hand-off contract to BOLT-002).</b> The assembly is a bounded
/// streaming write, so a refused result does not imply the destination stream is empty —
/// it may hold the bytes of earlier non-repeated fragments (never the offending reference,
/// never a complete <c>&lt;/html&gt;</c>, never a non-contained asset). When
/// <see cref="IsProduced"/> is <see langword="false"/> the caller MUST discard the
/// destination stream and publish nothing (ADR-046 §3.5 item 1); only a produced result is
/// a complete, self-contained document.
/// </para>
/// </summary>
public sealed class HtmlAssemblyResult
{
    private HtmlAssemblyResult(HtmlAssemblyOutcome outcome, HtmlRefusal? refusal)
    {
        Outcome = outcome;
        Refusal = refusal;
    }

    /// <summary>Which outcome this is.</summary>
    public HtmlAssemblyOutcome Outcome { get; }

    /// <summary>The refusal reason when <see cref="Outcome"/> is <see cref="HtmlAssemblyOutcome.Refused"/>; otherwise <see langword="null"/>.</summary>
    public HtmlRefusal? Refusal { get; }

    /// <summary>Whether one self-contained <c>.html</c> was produced.</summary>
    public bool IsProduced => Outcome == HtmlAssemblyOutcome.Produced;

    /// <summary>A produced result.</summary>
    public static HtmlAssemblyResult Produced() => new(HtmlAssemblyOutcome.Produced, null);

    /// <summary>A refused result naming the first offending reference.</summary>
    /// <param name="refusal">The refusal reason.</param>
    public static HtmlAssemblyResult Refused(HtmlRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return new HtmlAssemblyResult(HtmlAssemblyOutcome.Refused, refusal);
    }
}

/// <summary>The outcome of inlining one packaged reference: the inlined text, or a refusal.</summary>
/// <param name="Inlined">Whether the reference was inlined.</param>
/// <param name="Content">The inlined content (CSS text, or a <c>data:</c> URI) when inlined; otherwise <see langword="null"/>.</param>
/// <param name="Refusal">The refusal reason when not inlined; otherwise <see langword="null"/>.</param>
public readonly record struct HtmlInlineOutcome(bool Inlined, string? Content, HtmlRefusal? Refusal)
{
    /// <summary>An inlined outcome carrying its content.</summary>
    /// <param name="content">The inlined content.</param>
    public static HtmlInlineOutcome Ok(string content) => new(true, content, null);

    /// <summary>A refused outcome carrying its reason.</summary>
    /// <param name="refusal">The refusal reason.</param>
    public static HtmlInlineOutcome Refuse(HtmlRefusal refusal) => new(false, null, refusal);
}
