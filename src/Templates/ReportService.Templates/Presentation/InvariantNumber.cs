using System.Globalization;
using System.Numerics;

namespace ReportService.Templates;

/// <summary>
/// The culture-invariant number a machine reads: <c>.</c> as the decimal separator, no group
/// separator, no exponent. It reads only plain invariant decimal text or an engine's own
/// number, so a culture-formatted text such as <c>41,82</c> is refused and never read as
/// another number.
/// </summary>
public static class InvariantNumber
{
    /// <summary>The most decimals a template may ask for.</summary>
    public const int MaximumDecimals = 28;

    /// <summary>Reads <paramref name="value"/> as an exact decimal.</summary>
    /// <param name="value">Plain invariant decimal text, or an engine's own number.</param>
    /// <param name="number">The decimal, when the value carries one.</param>
    public static bool TryRead(object? value, out decimal number)
    {
        number = 0m;
        switch (value)
        {
            case string text:
                return TryParsePlain(text, out number);
            case decimal exact:
                number = exact;
                return true;
            case double real:
                return TryFromShortest(real.ToString("R", CultureInfo.InvariantCulture), double.IsFinite(real), out number);
            case float single:
                return TryFromShortest(single.ToString("R", CultureInfo.InvariantCulture), float.IsFinite(single), out number);
            case int or long or short or sbyte or byte or ushort or uint:
                number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            case ulong large:
                number = large;
                return true;
            case BigInteger big when big >= (BigInteger)decimal.MinValue && big <= (BigInteger)decimal.MaxValue:
                number = (decimal)big;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads the number of decimals a template asked for.</summary>
    /// <param name="value">A whole number from 0 to 28, as an engine's own integer or as digit text.</param>
    /// <param name="places">The number of decimals.</param>
    public static bool TryReadDecimals(object? value, out int places)
    {
        places = 0;
        decimal number;
        switch (value)
        {
            case string text when text.Length is > 0 and <= 2 && text.All(char.IsAsciiDigit):
                number = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
                break;
            case string:
                return false;
            default:
                if (!TryRead(value, out number))
                {
                    return false;
                }

                break;
        }

        if (number != decimal.Truncate(number) || number < 0 || number > MaximumDecimals)
        {
            return false;
        }

        places = (int)number;
        return true;
    }

    /// <summary>Writes <paramref name="number"/> with its own scale.</summary>
    /// <param name="number">The decimal.</param>
    public static string Write(decimal number)
    {
        var text = number.ToString(CultureInfo.InvariantCulture);
        return number == 0m && text.StartsWith('-') ? text[1..] : text;
    }

    /// <summary>Writes <paramref name="number"/> rounded half away from zero to <paramref name="places"/> decimals.</summary>
    /// <param name="number">The decimal.</param>
    /// <param name="places">The number of decimals, 0 to 28.</param>
    public static string Write(decimal number, int places)
    {
        var rounded = Math.Round(number, places, MidpointRounding.AwayFromZero);
        var text = rounded.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return rounded == 0m && text.StartsWith('-') ? text[1..] : text;
    }

    private static bool TryParsePlain(string text, out decimal number)
    {
        number = 0m;
        if (text.Length == 0)
        {
            return false;
        }

        var digits = 0;
        var points = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsAsciiDigit(character))
            {
                digits++;
            }
            else if (character == '.' && points == 0 && digits > 0)
            {
                points++;
            }
            else if (character != '-' || index != 0)
            {
                return false;
            }
        }

        return digits > 0
            && !text.EndsWith('.')
            && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    // A binary floating-point number is read through its shortest round-trip text, so 1/3
    // writes 0.3333333333333333 and 41.82f writes 41.82, never the binary expansion.
    private static bool TryFromShortest(string roundTrip, bool finite, out decimal number)
    {
        number = 0m;
        return finite
            && decimal.TryParse(roundTrip, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
