using System.Text;

namespace TextIntegrityCheck;

/// <summary>A difference between an expected and an actual normalised text.</summary>
internal sealed record TextDifference(string Class, int Offset, string ExpectedContext, string ActualContext);

/// <summary>
/// Normalisation N, applied identically to the extracted and to the expected
/// text: Unicode NFKC, then every whitespace character removed — nothing else
/// (no case folding, no punctuation mapping, no de-hyphenation).
/// </summary>
internal static class TextNormalisation
{
    private const int Context = 40;

    public static string N(string text)
    {
        var normalised = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalised.Length);
        foreach (var c in normalised)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Compares two normalised texts. Strips the common prefix and suffix: an
    /// empty actual middle is "text-dropped"; an empty expected middle is
    /// "text-repeated" when the inserted span equals the expected text just
    /// before it, else "text-altered"; anything else is "text-altered".
    /// </summary>
    public static TextDifference? Compare(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return null;
        }

        var limit = Math.Min(expected.Length, actual.Length);
        var prefix = 0;
        while (prefix < limit && expected[prefix] == actual[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < limit - prefix && expected[expected.Length - 1 - suffix] == actual[actual.Length - 1 - suffix])
        {
            suffix++;
        }

        var expectedMiddle = expected.Length - prefix - suffix;
        var actualMiddle = actual.Length - prefix - suffix;
        string kind;
        if (actualMiddle == 0)
        {
            kind = "text-dropped";
        }
        else if (expectedMiddle == 0)
        {
            var inserted = actual.AsSpan(prefix, actualMiddle);
            kind = prefix >= actualMiddle && expected.AsSpan(prefix - actualMiddle, actualMiddle).SequenceEqual(inserted) ? "text-repeated" : "text-altered";
        }
        else
        {
            kind = "text-altered";
        }

        return new TextDifference(kind, prefix, Around(expected, prefix), Around(actual, prefix));
    }

    /// <summary>True when the text holds a replacement character or a private-use code point (an unmapped glyph).</summary>
    public static bool HasUnmappedGlyph(string text)
    {
        foreach (var c in text)
        {
            if (c == '�' || (c >= '' && c <= ''))
            {
                return true;
            }
        }

        return false;
    }

    private static string Around(string text, int offset)
    {
        var start = Math.Max(0, offset - Context);
        var end = Math.Min(text.Length, offset + Context);
        return text[start..end];
    }
}
