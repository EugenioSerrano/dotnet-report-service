using System.Globalization;
using System.Text;

namespace ReportService.Testing.Fixtures;

/// <summary>
/// The unique-token text of a fixture: words from a fixed list, chosen by a
/// seeded generator and joined by one space, with a counter word every sixth
/// token. No two lines of a generated description or paragraph are equal, so a
/// dropped or a repeated line cannot hide behind an identical neighbour.
/// </summary>
public static class UniqueText
{
    /// <summary>The base seed of every generated text.</summary>
    public const ulong BaseSeed = 20261022UL;

    /// <summary>The forty-eight words: ligature-prone, accented and plain.</summary>
    public static IReadOnlyList<string> Words { get; } =
    [
        "office", "affine", "flow", "final", "efficient", "ruffle", "fjord", "waffle",
        "año", "acción", "información", "pequeño", "über", "Ángulo", "Éxito", "Ñandú",
        "valve", "pump", "rail", "cargo", "meter", "panel", "drill", "joint",
        "sensor", "cable", "wiring", "nozzle", "hinge", "gasket", "filter", "lamp",
        "bracket", "voltage", "energy", "journey", "freight", "delivery", "service", "yard",
        "zone", "network", "balance", "labour", "pallet", "switch", "engine", "quarterly",
    ];

    /// <summary>Whether a generated text may hold <paramref name="value"/>.</summary>
    public static bool IsAllowed(char value) =>
        (value >= ' ' && value <= '~') || "áéíóúñüÁÉÑ".Contains(value, StringComparison.Ordinal);

    /// <summary>The seed of one text: the base seed combined with the hash of "id/field".</summary>
    public static ulong Seed(string fixtureId, string field) =>
        BaseSeed ^ Fnv1a.Hash64(Encoding.UTF8.GetBytes($"{fixtureId}/{field}"));

    /// <summary>The first <paramref name="count"/> tokens of the text of this fixture and field.</summary>
    public static List<string> Tokens(string fixtureId, string field, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var random = new SplitMix64(Seed(fixtureId, field));
        var tokens = new List<string>(count);
        var counter = 0;
        for (var index = 1; index <= count; index++)
        {
            if (index % 6 == 0)
            {
                counter++;
                tokens.Add("n" + counter.ToString("D7", CultureInfo.InvariantCulture));
            }
            else
            {
                tokens.Add(Words[(int)(random.Next() % (ulong)Words.Count)]);
            }
        }

        return tokens;
    }

    /// <summary>The token stream joined by one space, longer than <paramref name="minimumLength"/>.</summary>
    public static string Stream(string fixtureId, string field, int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);

        var count = (minimumLength / 4) + 16;
        while (true)
        {
            var text = string.Join(' ', Tokens(fixtureId, field, count));
            if (text.Length > minimumLength)
            {
                return text;
            }

            count *= 2;
        }
    }

    /// <summary>
    /// The stream cut at exactly <paramref name="length"/> characters. A cut that
    /// would end in a space takes the next character instead, so the length is
    /// exact and the text never ends in whitespace.
    /// </summary>
    public static string Cut(string stream, int length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (length >= stream.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The stream is too short for the cut.");
        }

        var text = stream[..length];
        return text[^1] == ' '
            ? string.Concat(stream.AsSpan(0, length - 1), stream.AsSpan(length, 1))
            : text;
    }

    /// <summary>A unique-token text of exactly <paramref name="length"/> characters.</summary>
    public static string OfLength(string fixtureId, string field, int length) =>
        Cut(Stream(fixtureId, field, length), length);

    /// <summary>
    /// The longest cut of the stream whose measured size is at most
    /// <paramref name="maximum"/> and at least <paramref name="minimum"/>.
    /// </summary>
    public static string OfSize(string fixtureId, string field, int minimum, int maximum, Func<string, int> sizeOf)
    {
        ArgumentNullException.ThrowIfNull(sizeOf);

        var stream = Stream(fixtureId, field, maximum);
        var text = Cut(stream, LargestCut(stream, maximum, sizeOf));
        var size = sizeOf(text);
        if (size < minimum || size > maximum)
        {
            throw new InvalidOperationException(
                $"{fixtureId}/{field}: the measured size {size} is outside [{minimum}, {maximum}].");
        }

        return text;
    }

    /// <summary>
    /// A unique-token text whose measured size is exactly
    /// <paramref name="size"/>. The stream is cut at the largest length whose
    /// size does not exceed the target and then extended one character at a time.
    /// </summary>
    public static string OfExactSize(string fixtureId, string field, int size, Func<string, int> sizeOf)
    {
        ArgumentNullException.ThrowIfNull(sizeOf);

        var stream = Stream(fixtureId, field, size);
        var length = LargestCut(stream, size, sizeOf);
        while (length + 1 < stream.Length && sizeOf(Cut(stream, length)) < size)
        {
            length++;
        }

        var text = Cut(stream, length);
        var measured = sizeOf(text);
        if (measured != size)
        {
            throw new InvalidOperationException(
                $"{fixtureId}/{field}: the measured size {measured} is not exactly {size}.");
        }

        return text;
    }

    private static int LargestCut(string stream, int maximum, Func<string, int> sizeOf)
    {
        var low = 1;
        var high = stream.Length - 1;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (sizeOf(Cut(stream, middle)) <= maximum)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }
}
