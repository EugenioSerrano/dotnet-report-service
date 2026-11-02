namespace ReportService.Templates;

/// <summary>
/// The values one part sees. Every member is made of plain values only —
/// objects, arrays, strings, booleans, <see langword="null"/> and numbers given
/// as text — so a template can reach nothing but this model: no service object
/// is exposed and no type is registered for member access.
/// </summary>
/// <param name="Header">The header as received.</param>
/// <param name="Item">The current record, or <see langword="null"/> in a document part.</param>
/// <param name="Group">The current group, or <see langword="null"/> outside groups.</param>
/// <param name="Block">The block being composed.</param>
/// <param name="Aggregates">The declared accumulators; empty in this increment.</param>
/// <param name="Render">The fixed culture, time zone and clock of the job.</param>
public sealed record RenderContext(
    IReadOnlyDictionary<string, object?> Header,
    IReadOnlyDictionary<string, object?>? Item,
    IReadOnlyDictionary<string, object?>? Group,
    IReadOnlyDictionary<string, object?> Block,
    IReadOnlyDictionary<string, object?> Aggregates,
    IReadOnlyDictionary<string, object?> Render)
{
    /// <summary>The member names of the model, in the order they are exposed.</summary>
    public static IReadOnlyList<string> MemberNames { get; } = ["header", "item", "group", "block", "aggregates", "render"];

    /// <summary>The members that are present for this context, by name.</summary>
    public IEnumerable<KeyValuePair<string, object?>> Members()
    {
        // A part that has no record or no group still sees an empty member, so a
        // template printing one prints nothing rather than failing the block.
        yield return new("header", Header);
        yield return new("item", Item ?? Empty);
        yield return new("group", Group ?? Empty);
        yield return new("block", Block);
        yield return new("aggregates", Aggregates);
        yield return new("render", Render);
    }

    private static IReadOnlyDictionary<string, object?> Empty { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
