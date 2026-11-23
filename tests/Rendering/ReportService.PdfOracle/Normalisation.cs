using System.Text;

namespace ReportService.PdfOracle;

/// <summary>A normalised difference between an expected and a printed text.</summary>
/// <param name="Class">What kind of difference it is: dropped, repeated or altered text.</param>
/// <param name="Offset">Where in the expected text the difference starts.</param>
internal sealed record TextDifference(string Class, int Offset);

/// <summary>
/// The normalisation both sides of a comparison go through: Unicode compatibility
/// form, then every whitespace character removed — nothing else. No case folding,
/// no punctuation mapping, no de-hyphenation, so a hyphen the source does not hold
/// is a difference.
/// </summary>
internal static class Normalisation
{
    /// <summary>The normal form of <paramref name="text"/>.</summary>
    public static string N(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalised = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalised.Length);
        foreach (var character in normalised)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Compares two normalised texts, stripping the common prefix and suffix: text
    /// the printed side lacks is "text-dropped"; text it adds is "text-repeated"
    /// when the inserted span repeats what precedes it, else "text-altered"; a
    /// difference on both sides at once is "text-altered".
    /// </summary>
    public static TextDifference? Compare(string expected, string printed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(printed);

        if (string.Equals(expected, printed, StringComparison.Ordinal))
        {
            return null;
        }

        var limit = Math.Min(expected.Length, printed.Length);
        var prefix = 0;
        while (prefix < limit && expected[prefix] == printed[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < limit - prefix && expected[expected.Length - 1 - suffix] == printed[printed.Length - 1 - suffix])
        {
            suffix++;
        }

        var missing = expected.Length - prefix - suffix;
        var added = printed.Length - prefix - suffix;
        if (added == 0)
        {
            return new TextDifference("text-dropped", prefix);
        }

        if (missing == 0)
        {
            var inserted = printed.AsSpan(prefix, added);
            var repeatsBefore = prefix >= added && expected.AsSpan(prefix - added, added).SequenceEqual(inserted);
            return new TextDifference(repeatsBefore ? "text-repeated" : "text-altered", prefix);
        }

        return new TextDifference("text-altered", prefix);
    }

    /// <summary>
    /// Whether the text holds a replacement character or a private-use code point:
    /// a glyph the reader could not map to the character the source holds. Call it
    /// before normalising, on the text as it was read.
    /// </summary>
    public static bool HasUnmappedGlyph(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var character in text)
        {
            if (character == '\uFFFD' || (character >= '\uE000' && character <= '\uF8FF'))
            {
                return true;
            }
        }

        return false;
    }
}
