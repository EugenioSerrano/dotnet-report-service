namespace PaginationFixtures;

/// <summary>
/// The negative-control mutations. A harness applies one to an in-memory copy
/// of a fixture when it builds the HTML; the fixture file — the checker's
/// source — is never mutated. The checker reads only the declared mutation id,
/// to check that the control failed for the expected reason at the expected place.
/// </summary>
public static class ControlMutations
{
    public const string DropWord = "NC-W";
    public const string RepeatLine = "NC-L";
    public const string OmitRow = "NC-R";
    public const string DropFreeTextWord = "NC-F";

    public static IReadOnlyList<string> All { get; } = [DropWord, RepeatLine, OmitRow, DropFreeTextWord];

    /// <summary>The failure class and place the checker must report for a control.</summary>
    public static (string Class, int? Row, string? Region) Expected(string mutation) => mutation switch
    {
        DropWord => ("text-dropped", FixtureSeries.TallRowSequence, null),
        RepeatLine => ("text-repeated", FixtureSeries.TallRowSequence, null),
        OmitRow => ("row-lost", FixtureSeries.TallRowSequence + 1, null),
        DropFreeTextWord => ("text-dropped", null, "free-after"),
        _ => throw new ArgumentException($"Unknown mutation '{mutation}'.", nameof(mutation)),
    };

    /// <summary>The fixture as the mutated HTML prints it.</summary>
    public static Fixture Apply(Fixture fixture, string mutation)
    {
        switch (mutation)
        {
            case DropWord:
                return WithRow50(fixture, description =>
                {
                    var tokens = description.Split(' ').ToList();
                    tokens.RemoveAt(tokens.Count / 2);
                    return string.Join(' ', tokens);
                });
            case RepeatLine:
                return WithRow50(fixture, description =>
                {
                    var middle = description.Length / 2;
                    var copy = description[(middle - 80)..middle];
                    return string.Concat(description.AsSpan(0, middle), copy, description.AsSpan(middle));
                });
            case OmitRow:
                return fixture with { Rows = [.. fixture.Rows.Where(r => !(r.Kind == RowKind.Item && r.Sequence == FixtureSeries.TallRowSequence + 1))] };
            case DropFreeTextWord:
                {
                    var paragraphs = fixture.FreeTextAfter.Select(p => p.Split(' ').ToList()).ToList();
                    var remaining = 99;
                    foreach (var tokens in paragraphs)
                    {
                        if (remaining < tokens.Count)
                        {
                            tokens.RemoveAt(remaining);
                            break;
                        }

                        remaining -= tokens.Count;
                    }

                    return fixture with { FreeTextAfter = [.. paragraphs.Select(t => string.Join(' ', t))] };
                }

            default:
                throw new ArgumentException($"Unknown mutation '{mutation}'.", nameof(mutation));
        }
    }

    private static Fixture WithRow50(Fixture fixture, Func<string, string> change) => fixture with
    {
        Rows = [.. fixture.Rows.Select(r => r.Kind == RowKind.Item && r.Sequence == FixtureSeries.TallRowSequence ? r with { Description = change(r.Description!) } : r)],
    };
}
