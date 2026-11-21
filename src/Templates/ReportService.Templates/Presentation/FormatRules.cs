using System.Globalization;

namespace ReportService.Templates;

/// <summary>Which family of format string a presentation function accepts.</summary>
public enum FormatFamily
{
    /// <summary>A numeric format: an exact decimal's presentation.</summary>
    Numeric,

    /// <summary>A date or time format: a calendar date's or an instant's presentation.</summary>
    DateTime,
}

/// <summary>
/// The format strings a template may ask for: one of the family's standard specifiers, or
/// a custom pattern that carries at least one placeholder of its family, no longer than
/// 32 characters, and that the runtime accepts.
/// <para>
/// The rules are checked before any value is formatted, so a format that cannot work is
/// refused by the template's own defect rather than by the data it meets.
/// </para>
/// </summary>
public static class FormatRules
{
    /// <summary>The longest format a template may ask for.</summary>
    public const int MaximumLength = 32;

    /// <summary>The reason a format outside the rules is refused with.</summary>
    public const string RefusalReason = "format-string";

    private static readonly char[] NumericStandard = ['N', 'C', 'F', 'P', 'E', 'G'];

    private static readonly char[] DateStandard = ['d', 'D', 'f', 'F', 'g', 'G', 'M', 'O', 'R', 's', 't', 'T', 'u', 'U', 'Y'];

    private const string NumericPlaceholders = "0#";

    private const string DatePlaceholders = "dMyHhmsfFtzKg";

    private static readonly decimal NumericProbe = 1234.5678m;

    private static readonly DateTimeOffset DateProbe = new(2026, 11, 2, 2, 21, 56, TimeSpan.Zero);

    /// <summary>Whether <paramref name="format"/> is one the family admits and the runtime accepts.</summary>
    /// <param name="format">The format a template asked for.</param>
    /// <param name="family">Which family the function accepts.</param>
    /// <param name="reason">The stable reason, when the format is refused.</param>
    public static bool TryValidate(string? format, FormatFamily family, out string? reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(format) || format.Length > MaximumLength)
        {
            reason = RefusalReason;
            return false;
        }

        if (!IsStandard(format, family) && !IsCustom(format, family))
        {
            reason = RefusalReason;
            return false;
        }

        if (!TryProbe(format, family))
        {
            reason = RefusalReason;
            return false;
        }

        return true;
    }

    /// <summary>Formats <paramref name="value"/> with <paramref name="format"/> in <paramref name="culture"/>.</summary>
    /// <param name="value">The exact decimal to present.</param>
    /// <param name="format">A format the rules admitted.</param>
    /// <param name="culture">The job's culture.</param>
    /// <param name="text">The presented text, when the runtime accepts the format.</param>
    public static bool TryFormat(decimal value, string format, CultureInfo culture, out string? text)
    {
        ArgumentNullException.ThrowIfNull(culture);
        try
        {
            text = value.ToString(format, culture);
            return true;
        }
        catch (FormatException)
        {
            text = null;
            return false;
        }
    }

    /// <summary>Formats <paramref name="value"/> with <paramref name="format"/> in <paramref name="culture"/>.</summary>
    /// <param name="value">The instant to present, already converted to the job's zone.</param>
    /// <param name="format">A format the rules admitted.</param>
    /// <param name="culture">The job's culture.</param>
    /// <param name="text">The presented text, when the runtime accepts the format.</param>
    public static bool TryFormat(DateTimeOffset value, string format, CultureInfo culture, out string? text)
    {
        ArgumentNullException.ThrowIfNull(culture);
        try
        {
            text = value.ToString(format, culture);
            return true;
        }
        catch (FormatException)
        {
            text = null;
            return false;
        }
    }

    /// <summary>Formats <paramref name="value"/> with <paramref name="format"/> in <paramref name="culture"/>.</summary>
    /// <param name="value">The calendar date to present.</param>
    /// <param name="format">A format the rules admitted.</param>
    /// <param name="culture">The job's culture.</param>
    /// <param name="text">The presented text, when the runtime accepts the format.</param>
    public static bool TryFormat(DateOnly value, string format, CultureInfo culture, out string? text)
    {
        ArgumentNullException.ThrowIfNull(culture);
        try
        {
            text = value.ToString(format, culture);
            return true;
        }
        catch (FormatException)
        {
            text = null;
            return false;
        }
    }

    private static bool IsStandard(string format, FormatFamily family)
    {
        if (format.Length is < 1 or > 3)
        {
            return false;
        }

        var letters = family == FormatFamily.Numeric ? NumericStandard : DateStandard;
        if (Array.IndexOf(letters, format[0]) < 0)
        {
            return false;
        }

        for (var i = 1; i < format.Length; i++)
        {
            if (!char.IsAsciiDigit(format[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCustom(string format, FormatFamily family)
    {
        var placeholders = family == FormatFamily.Numeric ? NumericPlaceholders : DatePlaceholders;
        foreach (var character in format)
        {
            if (placeholders.Contains(character, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryProbe(string format, FormatFamily family)
    {
        try
        {
            _ = family == FormatFamily.Numeric
                ? NumericProbe.ToString(format, CultureInfo.InvariantCulture)
                : DateProbe.ToString(format, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
