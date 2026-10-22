namespace XlsxWriterSpike.Typing;

/// <summary>
/// A number text reduced to its significant digits and the position of the
/// decimal point, so two texts can be compared for exact equality of value
/// without going through any binary type. <c>1000.000</c>, <c>1E3</c> and
/// <c>1000</c> reduce to the same value.
/// </summary>
/// <param name="Negative">Sign (always <c>false</c> for zero).</param>
/// <param name="Digits">Significant digits without leading or trailing zeros (<c>0</c> for zero).</param>
/// <param name="PointPosition">Number of digits before the decimal point (may be negative or exceed the digit count).</param>
internal readonly record struct NumberText(bool Negative, string Digits, long PointPosition)
{
    /// <summary>Count of significant digits; zero counts as 1.</summary>
    public int SignificantDigits => Digits.Length;

    /// <summary>Parses a JSON-style number text (<c>-?digits(.digits)?([eE][+-]?digits)?</c>).</summary>
    public static bool TryParse(string text, out NumberText value)
    {
        value = default;
        var span = text.AsSpan().Trim();
        var negative = false;
        if (span.Length > 0 && (span[0] == '-' || span[0] == '+'))
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        var exponentAt = span.IndexOfAny('e', 'E');
        long exponent = 0;
        if (exponentAt >= 0)
        {
            if (!long.TryParse(span[(exponentAt + 1)..], System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out exponent)
                || exponent is > int.MaxValue or < int.MinValue)
            {
                return false;
            }

            span = span[..exponentAt];
        }

        var pointAt = span.IndexOf('.');
        var integerPart = pointAt >= 0 ? span[..pointAt] : span;
        var fractionPart = pointAt >= 0 ? span[(pointAt + 1)..] : [];
        if (integerPart.Length + fractionPart.Length == 0
            || integerPart.ContainsAnyExceptInRange('0', '9')
            || fractionPart.ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        var digits = string.Concat(integerPart, fractionPart);
        long point = integerPart.Length + exponent;
        var first = 0;
        while (first < digits.Length && digits[first] == '0')
        {
            first++;
            point--;
        }

        var last = digits.Length - 1;
        while (last >= first && digits[last] == '0')
        {
            last--;
        }

        value = first > last
            ? new NumberText(false, "0", 1)
            : new NumberText(negative, digits[first..(last + 1)], point);
        return true;
    }
}
