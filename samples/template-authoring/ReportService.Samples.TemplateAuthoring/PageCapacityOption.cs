using System.Globalization;
using ReportService.TemplateChecks.PdfConstraints;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// The <c>--page-capacity</c> option of <c>render</c>: the page capacity the local check
/// measures the page-number stamp at. Its value is ASCII decimal digits only - no sign,
/// no surrounding whitespace, no separator - leading zeros allowed, denoting a whole
/// number greater than zero that fits a 32-bit integer.
/// </summary>
public static class PageCapacityOption
{
    /// <summary>The option's name on the command line.</summary>
    public const string Name = "--page-capacity";

    /// <summary>Reads the option's value.</summary>
    /// <param name="value">The value given, or <see langword="null"/> when the option was not given.</param>
    /// <returns>The capacity, or <see langword="null"/> when the option was not given.</returns>
    /// <exception cref="ArgumentException">The value is not a page count; the message names the option, never the value.</exception>
    public static NumberingCapacity? Parse(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pages) || pages <= 0)
        {
            throw new ArgumentException(Name + " must be a whole number of pages greater than zero.");
        }

        return new NumberingCapacity(pages);
    }
}
