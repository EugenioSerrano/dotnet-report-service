using System.Globalization;

namespace ReportService.Pdf.Assembly;

/// <summary>
/// The numbering contract a template declares for a multi-block PDF: where the
/// stamp goes, with which packaged font and size, in which wording and culture.
/// The wording carries exactly one <c>{X}</c> (the page's global position) and one
/// <c>{Y}</c> (the total page count), and nothing else is substituted.
/// </summary>
/// <param name="Area">The reserved rectangle, in millimetres.</param>
/// <param name="Font">The packaged font and size.</param>
/// <param name="Format">The wording, with one <c>{X}</c> and one <c>{Y}</c>.</param>
/// <param name="Culture">The BCP 47 culture the numbers are written in.</param>
public sealed record PageNumbering(NumberingArea Area, NumberingFont Font, string Format, string Culture)
{
    /// <summary>The placeholder of the page's global position.</summary>
    public const string PositionPlaceholder = "{X}";

    /// <summary>The placeholder of the document's total page count.</summary>
    public const string TotalPlaceholder = "{Y}";

    /// <summary>The stamp of one page of a document of <paramref name="total"/> pages.</summary>
    /// <param name="position">The page's global position, from one.</param>
    /// <param name="total">The document's total page count.</param>
    /// <returns>The text the stamp carries.</returns>
    public string TextFor(int position, int total)
    {
        var culture = CultureInfo.GetCultureInfo(Culture);
        return Format
            .Replace(PositionPlaceholder, position.ToString(culture), StringComparison.Ordinal)
            .Replace(TotalPlaceholder, total.ToString(culture), StringComparison.Ordinal);
    }

    /// <summary>Whether a wording carries exactly one <c>{X}</c> and one <c>{Y}</c> and no other placeholder.</summary>
    /// <param name="format">The wording to check.</param>
    public static bool IsWellFormedFormat(string? format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return false;
        }

        var position = Count(format, PositionPlaceholder);
        var total = Count(format, TotalPlaceholder);
        if (position != 1 || total != 1)
        {
            return false;
        }

        var remaining = format
            .Replace(PositionPlaceholder, string.Empty, StringComparison.Ordinal)
            .Replace(TotalPlaceholder, string.Empty, StringComparison.Ordinal);
        return !remaining.Contains('{', StringComparison.Ordinal)
            && !remaining.Contains('}', StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
