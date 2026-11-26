using System.Globalization;
using ReportService.Templates;

namespace ReportService.Rendering.Accounting;

/// <summary>The amount of one record, as the accounting path reads and writes it.</summary>
internal static class AccountingAmounts
{
    /// <summary>
    /// Reads the record's amount from the declared field. A record whose amount is
    /// absent or not an exact decimal is refused: the accounting path never substitutes
    /// a zero for a missing value.
    /// </summary>
    /// <param name="values">The record's values, as the render context exposes them.</param>
    /// <param name="field">The record field the manifest declared.</param>
    /// <param name="amount">The exact decimal value.</param>
    public static bool TryRead(IReadOnlyDictionary<string, object?> values, string field, out decimal amount)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrEmpty(field);
        amount = 0m;
        if (!values.TryGetValue(field, out var value) || value is null)
        {
            return false;
        }

        switch (value)
        {
            case decimal exact:
                amount = exact;
                return true;
            case int integer:
                amount = integer;
                return true;
            case long large:
                amount = large;
                return true;
            case string text:
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
            default:
                // A value the reader did not preserve exactly (a floating-point value, an
                // object, a boolean) is not an amount this path can print.
                return false;
        }
    }

    /// <summary>The exact invariant text of <paramref name="amount"/> with exactly <paramref name="scale"/> fraction digits.</summary>
    /// <param name="amount">The amount.</param>
    /// <param name="scale">The fraction digits the template declares.</param>
    public static string Text(decimal amount, int scale) =>
        amount.ToString("F" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
