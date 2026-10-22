namespace StreamingDataSpike.Rendering;

/// <summary>
/// The common render context given to the three engines: <c>header</c>,
/// <c>item</c>, <c>group</c>, <c>block</c>, <c>aggregates</c> and
/// <c>render</c>. Every number is exposed as its invariant text (a string),
/// because one engine drops the trailing zeros of decimals; see
/// <see cref="TemplateValues"/>.
/// </summary>
internal sealed class RenderContext
{
    /// <summary>The header, the same instance for the whole document.</summary>
    public required Dictionary<string, object?> Header { get; init; }

    /// <summary>The current record.</summary>
    public Dictionary<string, object?> Item { get; set; } = [];

    /// <summary><c>groupId</c> and <c>groupName</c> of the current group.</summary>
    public Dictionary<string, object?> Group { get; set; } = [];

    /// <summary><c>index</c> (1-based) and <c>recordCount</c> of the current block.</summary>
    public Dictionary<string, object?> Block { get; set; } = [];

    /// <summary><c>groupAmount</c>: running exact sum of <c>amount</c> within the current group.</summary>
    public Dictionary<string, object?> Aggregates { get; set; } = [];

    /// <summary>Culture, time zone and generation time, fixed for the whole document.</summary>
    public required Dictionary<string, object?> Render { get; init; }

    /// <summary>
    /// When <see langword="true"/> an engine keeps its own context in
    /// <see cref="EngineState"/> and only updates the values (one context per
    /// block); otherwise it builds a new context for every render.
    /// </summary>
    public bool ReuseEngineState { get; init; }

    /// <summary>The engine's own context while <see cref="ReuseEngineState"/> is set.</summary>
    public object? EngineState { get; set; }
}
