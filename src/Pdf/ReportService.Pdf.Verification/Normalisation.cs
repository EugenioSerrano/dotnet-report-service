using System.Text;

namespace ReportService.Pdf.Verification;

/// <summary>
/// The single normalisation both sides of a check are put through before they are
/// compared: Unicode NFKC, then every white-space character removed, and nothing
/// else. Case is kept, punctuation is kept, a hyphen is never joined: a rule that
/// changed any of those could make two different texts equal.
/// </summary>
public static class Normalisation
{
    /// <summary>The character a reader prints where it could not map a glyph.</summary>
    public const char ReplacementCharacter = '\uFFFD';

    /// <summary>Normalises <paramref name="text"/>: NFKC, then white space removed.</summary>
    /// <param name="text">The text to normalise.</param>
    public static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var folded = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(folded.Length);
        foreach (var character in folded)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds a character the document does not map:
    /// the replacement character, or a private-use code point. Such a character is
    /// never compared: a document that paints one fails, because what it prints is
    /// not what any composed text says.
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    public static bool HasUnmappedGlyph(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == ReplacementCharacter || IsPrivateUse(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds a white space character, which a printed
    /// surface drops when it justifies or wraps a line and therefore never separates
    /// two texts that must be compared.
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    public static bool HasWhiteSpace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Any(char.IsWhiteSpace);
    }

    private static bool IsPrivateUse(int codePoint) =>
        codePoint is >= 0xE000 and <= 0xF8FF
        or >= 0xF0000 and <= 0xFFFFD
        or >= 0x100000 and <= 0x10FFFD;
}
