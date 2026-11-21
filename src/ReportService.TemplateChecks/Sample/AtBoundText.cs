using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReportService.TemplateChecks.Sample;

/// <summary>
/// The value the take prints at the row bound: words of ASCII letters separated by
/// single ASCII spaces, at the measured word density the bound family records, and
/// cut so that the whole value is exactly the number of decoded UTF-8 bytes the
/// bound names. The builder counts what it built and refuses to hand back a value
/// that is not exactly the bound it was given: a fixture one byte off would measure
/// a different value than the one the guaranteed envelope was measured at.
/// <para>
/// The construction is deterministic: a fixed seed drives an own SplitMix64 and an
/// own word list, so two builds are byte-identical and no fixture library is
/// reached from product code. The value is ASCII, so its decoded byte count and its
/// character count are the same number.
/// </para>
/// <para>
/// The same builder constructs the values a contract's own <c>pattern</c> or its one
/// long-value <c>format</c> - <c>duration</c> - admits, from the pattern's literals and
/// character classes; a value it cannot construct is reported as <see langword="null"/>
/// and never passed off as one the contract admits.
/// </para>
/// </summary>
public static class AtBoundText
{
    /// <summary>The seed every build uses, so the value is the same on every host.</summary>
    public const ulong Seed = 20261120UL;

    /// <summary>The words a built value holds at the delivered bound.</summary>
    public const int Words = 35_705;

    /// <summary>The single spaces a built value holds at the delivered bound.</summary>
    public const int Spaces = 35_704;

    /// <summary>The letters a built value holds at the delivered bound.</summary>
    public const int Letters = 226_440;

    /// <summary>The seven-letter words the first part of the value is drawn from.</summary>
    private static readonly string[] SevenLetter = ["service", "network", "balance", "journey", "freight"];

    /// <summary>The six-letter words the second part of the value is drawn from.</summary>
    private static readonly string[] SixLetter = ["office", "sensor", "wiring", "nozzle", "gasket"];

    private const int LongWords = 12_210;

    private const int ShortWords = 23_495;

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Builds the at-bound value for <paramref name="boundBytes"/> decoded UTF-8 bytes.</summary>
    /// <param name="boundBytes">The bound the value must occupy exactly.</param>
    /// <returns>The value, exactly <paramref name="boundBytes"/> decoded bytes long.</returns>
    /// <exception cref="AtBoundValueException">The value the builder produced is not exactly the bound.</exception>
    public static string Build(int boundBytes) => Build(boundBytes, Seed);

    /// <summary>Builds the at-bound value from a stated seed.</summary>
    /// <param name="boundBytes">The bound the value must occupy exactly.</param>
    /// <param name="seed">The seed the word choice is drawn from.</param>
    /// <returns>The value, exactly <paramref name="boundBytes"/> decoded bytes long.</returns>
    /// <exception cref="AtBoundValueException">The value the builder produced is not exactly the bound.</exception>
    public static string Build(int boundBytes, ulong seed)
    {
        if (boundBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boundBytes), "The bound must be a positive number of bytes.");
        }

        var random = new SplitMix64(seed);
        var builder = new StringBuilder(boundBytes);

        for (var index = 0; index < LongWords; index++)
        {
            if (index > 0)
            {
                _ = builder.Append(' ');
            }

            _ = builder.Append(SevenLetter[(int)(random.Next() % (ulong)SevenLetter.Length)]);
        }

        for (var index = 0; index < ShortWords; index++)
        {
            _ = builder.Append(' ');
            _ = builder.Append(SixLetter[(int)(random.Next() % (ulong)SixLetter.Length)]);
        }

        var value = builder.ToString();
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes != boundBytes)
        {
            throw new AtBoundValueException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The builder produced {bytes} decoded UTF-8 bytes where the bound is {boundBytes}."));
        }

        return value;
    }

    /// <summary>
    /// Builds a value of exactly <paramref name="boundBytes"/> decoded UTF-8 bytes that
    /// satisfies <paramref name="pattern"/>, from the pattern's own literals and
    /// character classes, or answers <see langword="null"/> when it constructs none.
    /// </summary>
    /// <param name="pattern">The contract's pattern, as the schema writes it.</param>
    /// <param name="boundBytes">The bound the value must occupy exactly.</param>
    /// <returns>The value, or <see langword="null"/> when the builder constructs none that matches.</returns>
    public static string? BuildForPattern(string pattern, int boundBytes)
    {
        if (string.IsNullOrEmpty(pattern) || boundBytes <= 0)
        {
            return null;
        }

        var shape = PatternShape.Parse(pattern);
        if (shape is null || shape.FillCharacter is not { } fill)
        {
            return null;
        }

        var prefix = shape.Minimum;
        if (prefix.Length > boundBytes)
        {
            return null;
        }

        var wanted = boundBytes - prefix.Length;

        // The pattern's own shape decides how the rest is written: a space it admits is
        // used to keep the measured word density, a pattern that admits none is filled
        // with its own repeatable class run.
        if (shape.SpaceAdmitted)
        {
            var words = Extend(prefix, fill, wanted, words: true);
            if (Matches(pattern, words))
            {
                return words;
            }
        }

        var run = Extend(prefix, fill, wanted, words: false);
        return Matches(pattern, run) ? run : null;
    }

    /// <summary>
    /// Builds a value of exactly <paramref name="boundBytes"/> decoded UTF-8 bytes for the
    /// <c>duration</c> format - the one long-value format the delivered check admits - or
    /// answers <see langword="null"/> for a format that cannot hold the bound or that the
    /// builder does not construct.
    /// </summary>
    /// <param name="format">One of the profile's formats.</param>
    /// <param name="boundBytes">The bound the value must occupy exactly.</param>
    /// <returns>The value, or <see langword="null"/> when the format cannot hold the bound.</returns>
    public static string? BuildForFormat(string format, int boundBytes)
    {
        return format switch
        {
            "duration" when boundBytes > 3 => "P" + new string('0', boundBytes - 3) + "1D",
            _ => null,
        };
    }

    /// <summary>The lower-case hexadecimal SHA-256 of a value's decoded UTF-8 bytes.</summary>
    /// <param name="value">The value whose hash is taken.</param>
    /// <returns>The hash, never the value.</returns>
    public static string Sha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Extend(string prefix, char fill, int wanted, bool words)
    {
        var builder = new StringBuilder(prefix, prefix.Length + wanted + 8);
        if (wanted == 0)
        {
            return builder.ToString();
        }

        if (words && prefix.Length == 0)
        {
            _ = builder.Append(fill);
            wanted--;
        }

        while (wanted > 0)
        {
            if (words && wanted > 1)
            {
                _ = builder.Append(' ');
                wanted--;
            }

            var run = Math.Min(7, wanted);
            _ = builder.Append(fill, run);
            wanted -= run;
        }

        return builder.ToString();
    }

    private static bool Matches(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>One SplitMix64 sequence, the scheme the fixture library uses, held here.</summary>
    private struct SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    /// <summary>The shape a pattern's own literals and classes describe, as the builder reads it.</summary>
    private sealed record PatternShape(string Minimum, char? FillCharacter, bool SpaceAdmitted)
    {
        /// <summary>Reads the pattern's literals, classes and quantifiers, or answers null.</summary>
        /// <param name="pattern">The contract's pattern.</param>
        public static PatternShape? Parse(string pattern)
        {
            var body = pattern;
            if (body.StartsWith('^'))
            {
                body = body[1..];
            }

            if (body.EndsWith('$') && !body.EndsWith("\\$", StringComparison.Ordinal))
            {
                body = body[..^1];
            }

            var minimum = new StringBuilder();
            char? fill = null;
            var space = false;
            var index = 0;

            while (index < body.Length)
            {
                char[]? set;
                var character = body[index];
                if (character == '\\' && index + 1 < body.Length)
                {
                    var escaped = body[index + 1];
                    set = escaped switch
                    {
                        'd' => Digits(),
                        'w' => LettersAndDigits(),
                        's' => [' '],
                        _ => [escaped],
                    };
                    index += 2;
                }
                else if (character == '[')
                {
                    var close = body.IndexOf(']', index + 1);
                    if (close < 0)
                    {
                        return null;
                    }

                    set = Class(body[(index + 1)..close]);
                    index = close + 1;
                }
                else if (character == '.')
                {
                    set = LettersAndDigits();
                    index++;
                }
                else
                {
                    set = [character];
                    index++;
                }

                if (set is null || set.Length == 0)
                {
                    return null;
                }

                space |= set.Contains(' ');
                var repeat = Repeat(body, ref index, out _, out var unbounded);
                var count = repeat ?? 1;
                var chosen = Fill(set);
                if (chosen is not null)
                {
                    if (fill is null || unbounded)
                    {
                        fill = chosen;
                    }
                }
                else if (set.All(character => character == ' '))
                {
                    fill ??= ' ';
                }
                else if (unbounded)
                {
                    // A repeatable class of characters the builder cannot draw from: it can
                    // construct nothing from this pattern.
                    return null;
                }

                for (var written = 0; written < count; written++)
                {
                    _ = minimum.Append(set[0]);
                }

                if (unbounded)
                {
                    fill = chosen ?? fill;
                }
            }

            if (minimum.Length == 0)
            {
                return null;
            }

            // A space may be used only when the pattern itself admits one after the first
            // character; a pattern of digits and spaces admits it in its class.
            if (!space)
            {
                space = body.Contains(' ', StringComparison.Ordinal);
            }

            return new PatternShape(minimum.ToString(), fill, space);
        }

        private static int? Repeat(string body, ref int index, out bool atLeastOne, out bool unbounded)
        {
            atLeastOne = false;
            unbounded = false;
            if (index >= body.Length)
            {
                return null;
            }

            switch (body[index])
            {
                case '*':
                    index++;
                    unbounded = true;
                    return 0;
                case '+':
                    index++;
                    atLeastOne = true;
                    unbounded = true;
                    return 1;
                case '?':
                    index++;
                    return 0;
                case '{':
                    var close = body.IndexOf('}', index + 1);
                    if (close < 0)
                    {
                        return null;
                    }

                    var range = body[(index + 1)..close].Split(',');
                    index = close + 1;
                    if (!int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var low))
                    {
                        return null;
                    }

                    if (range.Length > 1)
                    {
                        unbounded = true;
                        atLeastOne = low > 0;
                    }

                    return low;
                default:
                    return null;
            }
        }

        private static char[]? Class(string body)
        {
            var characters = new List<char>();
            var negated = body.StartsWith('^');
            var index = negated ? 1 : 0;
            while (index < body.Length)
            {
                if (index + 2 < body.Length && body[index + 1] == '-')
                {
                    for (var character = body[index]; character <= body[index + 2]; character++)
                    {
                        characters.Add(character);
                    }

                    index += 3;
                    continue;
                }

                characters.Add(body[index]);
                index++;
            }

            if (!negated)
            {
                return [.. characters];
            }

            var admitted = LettersAndDigits().Concat([' ', '-', '_', '.']).ToHashSet();
            admitted.ExceptWith(characters);
            return [.. admitted];
        }

        private static char? Fill(char[] set)
        {
            foreach (var character in set)
            {
                if (character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                {
                    return character;
                }
            }

            return null;
        }

        private static char[] Digits() => [.. "0123456789".ToCharArray()];

        private static char[] LettersAndDigits() => [.. "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray()];
    }
}

/// <summary>The builder cannot produce a value that is exactly the bound it was given.</summary>
public sealed class AtBoundValueException(string message) : Exception(message);
