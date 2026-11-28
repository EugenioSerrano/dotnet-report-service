namespace ReportService.Data;

/// <summary>
/// Whether a JSON string value is an image data URI: its decoded text is, in full, the scheme
/// <c>data:</c>, one of the listed image media types, zero or more <c>;name=value</c>
/// parameters, the token <c>;base64</c>, a comma, and a body made only of the base64 alphabet
/// (<c>A-Z a-z 0-9 + /</c>) with at most two trailing <c>=</c>, of a length that is a multiple
/// of four and with no whitespace. The scheme, the media type and the <c>base64</c> token match
/// ASCII case-insensitively. Every other value - another media type, a percent-encoded data
/// URI, or a value that starts like an image data URI but whose body breaks the rule - is not
/// one. The value is read once from its written bytes, its escapes resolved on the fly, and
/// nothing is decoded into a copy or allocated.
/// </summary>
public static class ImageDataUri
{
    private static readonly byte[][] MediaTypes =
    [
        "image/png"u8.ToArray(),
        "image/jpeg"u8.ToArray(),
        "image/gif"u8.ToArray(),
        "image/webp"u8.ToArray(),
        "image/svg+xml"u8.ToArray(),
    ];

    /// <summary>Answers whether the value is an image data URI.</summary>
    /// <param name="rawValue">The string's bytes exactly as written between its quotes.</param>
    /// <param name="isEscaped">Whether the written bytes carry JSON escapes.</param>
    public static bool Is(ReadOnlySpan<byte> rawValue, bool isEscaped)
    {
        var text = new Decoder(rawValue, isEscaped);
        if (!MatchesIgnoringCase(ref text, "data:"u8))
        {
            return false;
        }

        if (!MediaType(ref text))
        {
            return false;
        }

        // Parameters, then ";base64" immediately before the comma.
        Span<byte> token = stackalloc byte[6];
        while (true)
        {
            if (text.Next() != (byte)';')
            {
                return false;
            }

            var length = 0;
            var hasEquals = false;
            var nameLength = 0;
            var valueLength = 0;
            int next;
            while ((next = text.Peek()) is not (-1 or ';' or ','))
            {
                text.Next();
                if (next is ' ' or '\t' or '\r' or '\n' or Decoder.NonAscii)
                {
                    return false;
                }

                if (next == '=' && !hasEquals)
                {
                    hasEquals = true;
                }
                else if (hasEquals)
                {
                    valueLength++;
                }
                else
                {
                    nameLength++;
                }

                if (length < token.Length)
                {
                    token[length] = (byte)next;
                }

                length++;
            }

            if (!hasEquals && length == 6 && next == ',' && EqualsIgnoringCase(token, "base64"u8))
            {
                text.Next();
                return Body(ref text);
            }

            if (!hasEquals || nameLength == 0 || valueLength == 0 || next != ';')
            {
                return false;
            }
        }
    }

    private static bool MediaType(ref Decoder text)
    {
        Span<byte> type = stackalloc byte[16];
        var length = 0;
        while (text.Peek() is var next and not (-1 or ';'))
        {
            if (length == type.Length || next == Decoder.NonAscii)
            {
                return false;
            }

            type[length++] = (byte)next;
            text.Next();
        }

        foreach (var listed in MediaTypes)
        {
            if (EqualsIgnoringCase(type[..length], listed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Body(ref Decoder text)
    {
        long length = 0;
        var padding = 0;
        int next;
        while ((next = text.Next()) != -1)
        {
            if (next == '=')
            {
                padding++;
                if (padding > 2)
                {
                    return false;
                }
            }
            else if (padding > 0 || !IsBase64(next))
            {
                return false;
            }

            length++;
        }

        return length % 4 == 0;
    }

    private static bool IsBase64(int value) =>
        value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/';

    private static bool MatchesIgnoringCase(ref Decoder text, ReadOnlySpan<byte> expected)
    {
        foreach (var letter in expected)
        {
            var next = text.Next();
            if (next == -1 || Lower(next) != letter)
            {
                return false;
            }
        }

        return true;
    }

    private static bool EqualsIgnoringCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> lowerCase)
    {
        if (value.Length != lowerCase.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (Lower(value[index]) != lowerCase[index])
            {
                return false;
            }
        }

        return true;
    }

    private static int Lower(int value) => value is >= 'A' and <= 'Z' ? value + 32 : value;

    // The decoded text of a JSON string, one byte at a time. Every character the grammar
    // admits is ASCII, so an escape that decodes to anything else is reported as one
    // non-ASCII marker, which no rule accepts.
    private ref struct Decoder
    {
        public const int NonAscii = 0x100;

        private readonly ReadOnlySpan<byte> raw;
        private readonly bool escaped;
        private int position;

        public Decoder(ReadOnlySpan<byte> raw, bool escaped)
        {
            this.raw = raw;
            this.escaped = escaped;
            position = 0;
        }

        public int Peek()
        {
            var saved = position;
            var value = Next();
            position = saved;
            return value;
        }

        public int Next()
        {
            if (position >= raw.Length)
            {
                return -1;
            }

            var current = raw[position++];
            if (current >= 0x80)
            {
                return NonAscii;
            }

            if (!escaped || current != (byte)'\\' || position >= raw.Length)
            {
                return current;
            }

            var escape = raw[position++];
            switch (escape)
            {
                case (byte)'"':
                case (byte)'\\':
                case (byte)'/':
                    return escape;
                case (byte)'b':
                    return 0x08;
                case (byte)'f':
                    return 0x0C;
                case (byte)'n':
                    return 0x0A;
                case (byte)'r':
                    return 0x0D;
                case (byte)'t':
                    return 0x09;
                case (byte)'u' when position + 4 <= raw.Length:
                    var code = 0;
                    for (var index = 0; index < 4; index++)
                    {
                        code = (code << 4) | HexValue(raw[position + index]);
                    }

                    position += 4;
                    return code is >= 0 and < 0x80 ? code : NonAscii;
                default:
                    return NonAscii;
            }
        }

        private static int HexValue(byte digit) => digit switch
        {
            >= (byte)'0' and <= (byte)'9' => digit - '0',
            >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => digit - 'A' + 10,
            _ => 0x10000,
        };
    }
}
