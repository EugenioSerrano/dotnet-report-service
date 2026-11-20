namespace ReportService.Data;

/// <summary>
/// The decoded length of a JSON string, from the raw span the reader yields. A value written
/// with no escape is its own length; a value written with escapes is walked once, and each
/// escape counts the bytes it becomes once decoded - a surrogate pair four, a lone surrogate
/// three, the replacement character it turns into. It allocates nothing and never decodes a
/// value, so a value the reader admitted but the runtime refuses to copy is still measured.
/// </summary>
public static class Utf8StringLength
{
    /// <summary>Counts the bytes <paramref name="rawValue"/> becomes once decoded.</summary>
    /// <param name="rawValue">The raw text of the string, without its quotes, as the reader yields it.</param>
    /// <param name="isEscaped">Whether the text carries escape sequences.</param>
    /// <returns>The decoded length in bytes of UTF-8; never more than the raw text's length.</returns>
    public static long Decoded(ReadOnlySpan<byte> rawValue, bool isEscaped)
    {
        if (!isEscaped)
        {
            return rawValue.Length;
        }

        long length = 0;
        for (var index = 0; index < rawValue.Length; index++)
        {
            if (rawValue[index] != (byte)'\\' || index + 1 == rawValue.Length)
            {
                length++;
                continue;
            }

            var escape = rawValue[++index];
            switch (escape)
            {
                case (byte)'"':
                case (byte)'\\':
                case (byte)'/':
                case (byte)'b':
                case (byte)'f':
                case (byte)'n':
                case (byte)'r':
                case (byte)'t':
                    length++;
                    break;

                case (byte)'u':
                    if (index + 4 >= rawValue.Length)
                    {
                        // No escape this reader admitted ends short; a span built by hand may,
                        // and the character it named is what is counted.
                        length++;
                        break;
                    }

                    var code = Hex(rawValue.Slice(index + 1, 4));
                    index += 4;

                    // A high surrogate followed at once by an escaped low surrogate is one
                    // character of four bytes, and both escapes belong to it.
                    if (code is >= 0xD800 and <= 0xDBFF
                        && index + 6 < rawValue.Length
                        && rawValue[index + 1] == (byte)'\\'
                        && rawValue[index + 2] == (byte)'u')
                    {
                        var low = Hex(rawValue.Slice(index + 3, 4));
                        if (low is >= 0xDC00 and <= 0xDFFF)
                        {
                            length += 4;
                            index += 6;
                            break;
                        }
                    }

                    // A lone surrogate is counted as the three bytes of the replacement
                    // character it becomes: nothing here decodes it.
                    length += code < 0x80 ? 1 : code < 0x800 ? 2 : 3;
                    break;

                default:
                    length++;
                    break;
            }
        }

        return length;
    }

    // The code unit of four hexadecimal digits, or the replacement character's when they
    // are not hexadecimal: the counters never throw on content the reader admitted.
    private static int Hex(ReadOnlySpan<byte> digits)
    {
        var value = 0;
        foreach (var digit in digits)
        {
            var nibble = digit switch
            {
                >= (byte)'0' and <= (byte)'9' => digit - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => digit - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => digit - (byte)'A' + 10,
                _ => -1,
            };

            if (nibble < 0)
            {
                return 0xFFFD;
            }

            value = (value << 4) | nibble;
        }

        return value;
    }
}
