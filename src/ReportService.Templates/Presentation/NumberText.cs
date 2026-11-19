using System.Globalization;

namespace ReportService.Templates;

/// <summary>
/// The number a template receives: the invariant text of an exact decimal value, or the
/// raw token of a number that has none.
/// <para>
/// A text presents an exact decimal when it carries no whitespace at all and parses as
/// one in the invariant culture. Whitespace is refused outright so that a digit group
/// separated by a thin space can never be read as a number the context meant to give as
/// text.
/// </para>
/// </summary>
public static class NumberText
{
    /// <summary>Reads <paramref name="text"/> as an exact decimal.</summary>
    /// <param name="text">The value as the render context holds it.</param>
    /// <param name="value">The exact decimal, when the text carries one.</param>
    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
