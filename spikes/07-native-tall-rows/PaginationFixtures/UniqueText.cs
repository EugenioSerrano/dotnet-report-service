using System.Text;

namespace PaginationFixtures;

/// <summary>SplitMix64 pseudo-random generator: small, fast and identical on every platform.</summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong state = seed;

    public ulong Next()
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>64-bit FNV-1a over bytes (never string.GetHashCode, which changes per process).</summary>
public static class Fnv1a
{
    public static ulong Hash64(ReadOnlySpan<byte> bytes)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 0x100000001B3UL;
        }

        return hash;
    }
}

/// <summary>
/// The unique-token text: words from a fixed list of 48, chosen by a seeded
/// generator and joined by one space; every 6th token is a counter token
/// ("n" and 7 digits), unique within the text, so no two printed lines of a
/// description or a paragraph are equal.
/// </summary>
public static class UniqueText
{
    public const ulong BaseSeed = 20261022UL;

    /// <summary>The 48 words: ligature-prone, accented and plain.</summary>
    public static readonly IReadOnlyList<string> Words =
    [
        "office", "affine", "flow", "final", "efficient", "ruffle", "fjord", "waffle",
        "año", "acción", "información", "pequeño", "über", "Ángulo", "Éxito", "Ñandú",
        "valve", "pump", "rail", "cargo", "meter", "panel", "drill", "joint",
        "sensor", "cable", "wiring", "nozzle", "hinge", "gasket", "filter", "lamp",
        "bracket", "voltage", "energy", "journey", "freight", "delivery", "service", "yard",
        "zone", "network", "balance", "labour", "pallet", "switch", "engine", "quarterly",
    ];

    /// <summary>The characters a unique-token text may hold (besides line breaks in hard-break fixtures).</summary>
    public static bool IsAllowed(char c) => (c >= ' ' && c <= '~') || "áéíóúñüÁÉÑ".Contains(c, StringComparison.Ordinal);

    /// <summary>The seed of a text: the base seed XOR the FNV-1a hash of "fixture id/field" in UTF-8.</summary>
    public static ulong Seed(string fixtureId, string field) => BaseSeed ^ Fnv1a.Hash64(Encoding.UTF8.GetBytes($"{fixtureId}/{field}"));

    /// <summary>The first <paramref name="count"/> tokens of the text of this fixture and field.</summary>
    public static List<string> Tokens(string fixtureId, string field, int count)
    {
        var random = new SplitMix64(Seed(fixtureId, field));
        var tokens = new List<string>(count);
        var counter = 0;
        for (var i = 1; i <= count; i++)
        {
            if (i % 6 == 0)
            {
                counter++;
                tokens.Add("n" + counter.ToString("D7", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                tokens.Add(Words[(int)(random.Next() % (ulong)Words.Count)]);
            }
        }

        return tokens;
    }

    /// <summary>The token stream joined by one space, at least <paramref name="minimumLength"/> + 1 characters long.</summary>
    public static string Stream(string fixtureId, string field, int minimumLength)
    {
        // Tokens average about seven characters with their space; take a margin and extend if short.
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
    /// would end in a space takes the next character instead of the space, so the
    /// length is exact and the text never ends in whitespace.
    /// </summary>
    public static string Cut(string stream, int length)
    {
        if (stream.Length <= length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The stream is too short for the cut.");
        }

        var text = stream[..length];
        return text[^1] == ' ' ? string.Concat(stream.AsSpan(0, length - 1), stream.AsSpan(length, 1)) : text;
    }

    /// <summary>A unique-token text of exactly <paramref name="length"/> characters.</summary>
    public static string OfLength(string fixtureId, string field, int length) => Cut(Stream(fixtureId, field, length), length);

    /// <summary>
    /// The longest cut of the stream whose size (as measured by <paramref name="sizeOf"/>)
    /// is at most <paramref name="maximum"/>; it must be at least <paramref name="minimum"/>.
    /// </summary>
    public static string OfSize(string fixtureId, string field, int minimum, int maximum, Func<string, int> sizeOf)
    {
        var stream = Stream(fixtureId, field, maximum);
        var low = 1;
        var high = maximum;
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

        var text = Cut(stream, low);
        var size = sizeOf(text);
        if (size < minimum || size > maximum)
        {
            throw new InvalidOperationException($"{fixtureId}/{field}: size {size} outside [{minimum}, {maximum}].");
        }

        return text;
    }
}
