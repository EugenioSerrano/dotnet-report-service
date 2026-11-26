using System.Globalization;
using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>The three labelled values one page's accounting band printed, or <see langword="null"/> for a line it lacks.</summary>
/// <param name="BroughtForward">The brought-forward value as printed, or <see langword="null"/>.</param>
/// <param name="PageSubtotal">The page-subtotal value as printed, or <see langword="null"/>.</param>
/// <param name="CarriedForward">The carried-forward value as printed, or <see langword="null"/>.</param>
public sealed record AccountingFooterLines(string? BroughtForward, string? PageSubtotal, string? CarriedForward)
{
    /// <summary>Whether all three lines were printed.</summary>
    public bool Complete => BroughtForward is not null && PageSubtotal is not null && CarriedForward is not null;
}

/// <summary>
/// Reads the accounting band of a printed page and writes and reads the values it
/// carries. The band is the template's own rectangle in the bottom margin: the reader
/// looks for the template's three labels there and takes the word that follows each as
/// the value. A value is written with exactly the declared fraction digits and no
/// group separator, in the block's presentation culture; a printed value is read back
/// under the same rule, so a value with a group separator or another scale is refused
/// rather than silently accepted as a similar number.
/// </summary>
public static class AccountingReading
{
    /// <summary>The fixed mark a row's own amount cell carries on every part but its last.</summary>
    public const string ContinuesMark = "(continues)";

    /// <summary>
    /// The normalised content text of a page with the fixed continuation mark removed.
    /// The mark is printed text of the accounting path, never composed text, so the
    /// order check compares the composed text with the page's own content only.
    /// </summary>
    /// <param name="page">The page as it was read.</param>
    public static string ContentTextWithoutMark(PrintedPage page) => ContentTextWithoutAccounting(page, expectedTotals: null);

    /// <summary>
    /// The normalised content text of a page with the accounting text the layout writes
    /// removed: the fixed continuation mark, and each expected group total once per
    /// occurrence it is expected to print (the totals travel as a multiset, so a page
    /// that ends two groups with the same total loses both). The totals come from the
    /// block's source records and the partition, never from the layout.
    /// </summary>
    /// <param name="page">The page as it was read.</param>
    /// <param name="expectedTotals">The remaining occurrences of each expected total text, already normalised; decremented as they are met.</param>
    public static string ContentTextWithoutAccounting(PrintedPage page, IDictionary<string, int>? expectedTotals)
    {
        ArgumentNullException.ThrowIfNull(page);
        var mark = Normalisation.Normalise(ContinuesMark);
        var builder = new System.Text.StringBuilder();
        foreach (var word in page.Words)
        {
            if (word.Band != ReadingBand.Content)
            {
                continue;
            }

            var text = Normalisation.Normalise(word.Text);
            if (string.Equals(text, mark, StringComparison.Ordinal))
            {
                continue;
            }

            if (expectedTotals is not null
                && expectedTotals.TryGetValue(text, out var remaining)
                && remaining > 0)
            {
                expectedTotals[text] = remaining - 1;
                continue;
            }

            _ = builder.Append(text);
        }

        return builder.ToString();
    }

    /// <summary>The three labelled values the page's accounting band printed.</summary>
    /// <param name="page">The page as it was read.</param>
    /// <param name="footer">The declared band and its labels.</param>
    public static AccountingFooterLines ReadFooter(PrintedPage page, AccountingFooter footer)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(footer);

        var words = page.Words
            .Where(word => word.Band == ReadingBand.Footer)
            .Select(word => Normalisation.Normalise(word.Text))
            .Where(text => text.Length > 0)
            .ToList();

        return new AccountingFooterLines(
            ValueAfter(words, footer.BroughtForward),
            ValueAfter(words, footer.PageSubtotal),
            ValueAfter(words, footer.CarriedForward));
    }

    /// <summary>
    /// The word that follows the label's own words, when the label was printed; the
    /// value is one word because it is written without a group separator.
    /// </summary>
    private static string? ValueAfter(IReadOnlyList<string> words, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var labelWords = label
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalisation.Normalise)
            .Where(text => text.Length > 0)
            .ToList();
        if (labelWords.Count == 0)
        {
            return null;
        }

        for (var start = 0; start + labelWords.Count <= words.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < labelWords.Count; offset++)
            {
                if (!string.Equals(words[start + offset], labelWords[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches && start + labelWords.Count < words.Count)
            {
                return words[start + labelWords.Count];
            }
        }

        return null;
    }

    /// <summary>The exact invariant text of <paramref name="amount"/> with exactly <paramref name="scale"/> fraction digits: the row's own amount.</summary>
    /// <param name="amount">The amount.</param>
    /// <param name="scale">The fraction digits the template declares.</param>
    public static string FormatAmount(decimal amount, int scale) =>
        amount.ToString("F" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Writes <paramref name="value"/> with exactly <paramref name="scale"/> fraction digits and no group separator.</summary>
    /// <param name="value">The value to write.</param>
    /// <param name="scale">The fraction digits the template declares.</param>
    /// <param name="culture">The block's presentation culture.</param>
    /// <param name="text">The written text.</param>
    public static bool TryFormat(decimal value, int scale, CultureInfo culture, out string text)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (scale is < 0 or > 9)
        {
            text = string.Empty;
            return false;
        }

        text = value.ToString("F" + scale.ToString(CultureInfo.InvariantCulture), culture);
        return true;
    }

    /// <summary>
    /// Reads a printed value under the declared scale and culture. The text must carry
    /// exactly <paramref name="scale"/> fraction digits after the culture's decimal
    /// separator and no group separator; anything else is refused.
    /// </summary>
    /// <param name="text">The printed text.</param>
    /// <param name="scale">The fraction digits the template declares.</param>
    /// <param name="culture">The block's presentation culture.</param>
    /// <param name="value">The value read.</param>
    public static bool TryParse(string? text, int scale, CultureInfo culture, out decimal value)
    {
        ArgumentNullException.ThrowIfNull(culture);
        value = 0m;
        if (string.IsNullOrWhiteSpace(text) || scale is < 0 or > 9)
        {
            return false;
        }

        var negative = text[0] == '-';
        var digits = negative ? text[1..] : text;
        var separator = culture.NumberFormat.NumberDecimalSeparator;
        string integer;
        string fraction;
        if (scale == 0)
        {
            integer = digits;
            fraction = string.Empty;
            if (digits.Contains(separator, StringComparison.Ordinal))
            {
                return false;
            }
        }
        else
        {
            var at = digits.IndexOf(separator, StringComparison.Ordinal);
            if (at <= 0 || at != digits.LastIndexOf(separator, StringComparison.Ordinal))
            {
                return false;
            }

            integer = digits[..at];
            fraction = digits[(at + separator.Length)..];
            if (fraction.Length != scale)
            {
                return false;
            }
        }

        if (integer.Length == 0)
        {
            return false;
        }

        foreach (var character in integer)
        {
            if (character is < '0' or > '9')
            {
                // A group separator, a sign or anything else printed inside the number is
                // not the value this check reads: the report prints no group separator.
                return false;
            }
        }

        foreach (var character in fraction)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        var invariant = (negative ? "-" : string.Empty)
            + integer
            + (scale == 0 ? string.Empty : "." + fraction);
        return decimal.TryParse(invariant, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Whether the word's rectangle lies inside the rectangle, both in points.</summary>
    /// <param name="word">The printed word.</param>
    /// <param name="left">The rectangle's left edge.</param>
    /// <param name="top">The rectangle's top edge.</param>
    /// <param name="width">The rectangle's width.</param>
    /// <param name="height">The rectangle's height.</param>
    public static bool IsInside(PrintedWord word, double left, double top, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(word);
        var centreX = (word.Left + word.Right) / 2d;
        var centreY = word.VerticalCentre;
        return centreX >= left && centreX <= left + width && centreY >= top && centreY <= top + height;
    }
}
