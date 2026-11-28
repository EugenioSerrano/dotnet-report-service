namespace ReportService.Templates;

/// <summary>Why a presentation function refused the value it was given.</summary>
public enum PresentationRefusal
{
    /// <summary>The value is not the invariant text of an exact decimal.</summary>
    NoDecimal,

    /// <summary>The value is not a date in the accepted forms, or an operand of a comparison is missing.</summary>
    NotADate,

    /// <summary>The format is outside its family, longer than the bound, or rejected by the runtime.</summary>
    BadFormat,
}

/// <summary>What one presentation function answered: text, a flag, or a typed refusal.</summary>
public abstract record PresentationResult
{
    private PresentationResult()
    {
    }

    /// <summary>Text to print.</summary>
    /// <param name="Value">The presented text.</param>
    public sealed record Text(string Value) : PresentationResult;

    /// <summary>A boolean answer, for a comparison used as a condition.</summary>
    /// <param name="Value">The answer.</param>
    public sealed record Flag(bool Value) : PresentationResult;

    /// <summary>The value or the format cannot be presented.</summary>
    /// <param name="Reason">Why.</param>
    public sealed record Refused(PresentationRefusal Reason) : PresentationResult;
}

/// <summary>
/// The ten presentation functions, bound to one block's settings. Every result is a pure
/// function of the value, the format and the settings: no function reads the host clock or
/// the process culture, and none accepts a culture, a time zone or a clock an author could
/// pass to override the job's own.
/// </summary>
public sealed class PresentationFunctions(PresentationSettings settings)
{
    /// <summary>Presents a number in the job's culture.</summary>
    /// <param name="value">The value as the render context holds it.</param>
    /// <param name="format">The numeric format the template asked for.</param>
    public PresentationResult FormatNumber(object? value, string? format)
    {
        if (!FormatRules.TryValidate(format, FormatFamily.Numeric, out _))
        {
            return new PresentationResult.Refused(PresentationRefusal.BadFormat);
        }

        if (value is not string text || !NumberText.TryParseDecimal(text, out var number))
        {
            return new PresentationResult.Refused(PresentationRefusal.NoDecimal);
        }

        return FormatRules.TryFormat(number, format!, settings.Culture, out var presented)
            ? new PresentationResult.Text(presented!)
            : new PresentationResult.Refused(PresentationRefusal.BadFormat);
    }

    /// <summary>Presents a date or an instant in the job's culture and time zone.</summary>
    /// <param name="value">The value as the render context holds it.</param>
    /// <param name="format">The date or time format the template asked for.</param>
    public PresentationResult FormatDate(object? value, string? format)
    {
        if (!FormatRules.TryValidate(format, FormatFamily.DateTime, out _))
        {
            return new PresentationResult.Refused(PresentationRefusal.BadFormat);
        }

        if (value is not string text || !IsoDate.TryClassify(text, out var date) || date is null)
        {
            return new PresentationResult.Refused(PresentationRefusal.NotADate);
        }

        var presented = date switch
        {
            IsoDate.CalendarDate calendar when FormatRules.TryFormat(calendar.Value, format!, settings.Culture, out var day) => day,
            IsoDate.Moment moment when FormatRules.TryFormat(IsoDate.ToZoned(moment.Value, settings.TimeZone), format!, settings.Culture, out var instant) => instant,
            _ => null,
        };

        return presented is null
            ? new PresentationResult.Refused(PresentationRefusal.BadFormat)
            : new PresentationResult.Text(presented);
    }

    /// <summary>Whether <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public PresentationResult NumGreaterThan(object? left, object? right) => Compare(left, right, static (first, second) => first > second);

    /// <summary>Whether <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public PresentationResult NumGreaterOrEqual(object? left, object? right) => Compare(left, right, static (first, second) => first >= second);

    /// <summary>Whether <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public PresentationResult NumLessThan(object? left, object? right) => Compare(left, right, static (first, second) => first < second);

    /// <summary>Whether <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public PresentationResult NumLessOrEqual(object? left, object? right) => Compare(left, right, static (first, second) => first <= second);

    /// <summary>Whether <paramref name="left"/> equals <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public PresentationResult NumEqual(object? left, object? right) => Compare(left, right, static (first, second) => first == second);

    /// <summary>Whether <paramref name="left"/> differs from <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public PresentationResult NumNotEqual(object? left, object? right) => Compare(left, right, static (first, second) => first != second);

    /// <summary>The sign of a number as text for printing: <c>-1</c>, <c>0</c> or <c>1</c>.</summary>
    /// <param name="value">The value as the render context holds it.</param>
    public PresentationResult NumSign(object? value)
    {
        if (value is not string text || !NumberText.TryParseDecimal(text, out var number))
        {
            return new PresentationResult.Refused(PresentationRefusal.NoDecimal);
        }

        return new PresentationResult.Text(number > 0 ? "1" : number < 0 ? "-1" : "0");
    }

    /// <summary>
    /// Writes a number culture-invariantly, for a position a machine reads (an attribute or a
    /// CSS length): <c>.</c> as the decimal separator, no group separator, no exponent. The
    /// job's culture is not read.
    /// </summary>
    /// <param name="value">Invariant plain decimal text, or an engine's own number.</param>
    /// <param name="decimals">
    /// <see langword="null"/> to keep the value's own scale, or a whole number from 0 to 28 to
    /// round half away from zero and write exactly that many decimals.
    /// </param>
    public PresentationResult NumInvariant(object? value, object? decimals)
    {
        if (!InvariantNumber.TryRead(value, out var number))
        {
            return new PresentationResult.Refused(PresentationRefusal.NoDecimal);
        }

        if (decimals is null)
        {
            return new PresentationResult.Text(InvariantNumber.Write(number));
        }

        if (!InvariantNumber.TryReadDecimals(decimals, out var places))
        {
            return new PresentationResult.Refused(PresentationRefusal.BadFormat);
        }

        return new PresentationResult.Text(InvariantNumber.Write(number, places));
    }

    private static PresentationResult Compare(object? left, object? right, Func<decimal, decimal, bool> compare)
    {
        if (!TryOperand(left, out var first, out var leftRefusal))
        {
            return new PresentationResult.Refused(leftRefusal);
        }

        if (!TryOperand(right, out var second, out var rightRefusal))
        {
            return new PresentationResult.Refused(rightRefusal);
        }

        return new PresentationResult.Flag(compare(first, second));
    }

    private static bool TryOperand(object? value, out decimal number, out PresentationRefusal refusal)
    {
        number = 0m;
        refusal = PresentationRefusal.NotADate;
        if (value is null)
        {
            return false;
        }

        if (value is not string text || text.Length == 0)
        {
            return false;
        }

        if (!NumberText.TryParseDecimal(text, out number))
        {
            refusal = PresentationRefusal.NoDecimal;
            return false;
        }

        return true;
    }
}
