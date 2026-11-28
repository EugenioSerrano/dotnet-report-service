using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ReportService.Data;

/// <summary>
/// The row bound of one detail record, read from the record's own bytes in one forward pass:
/// the first string value, in document order and at any depth, whose decoded UTF-8 length is
/// above the bound. Property names are never measured - a name is not a field - and numbers,
/// booleans and nulls carry no text. A value whose written span is within the bound is settled
/// by that span alone, because decoding never yields more bytes than the text it was written
/// with, so an ordinary record is scanned with no allocation and no decoding at all. The
/// pointer is built only for the value that is found, and no value is ever decoded or copied.
/// </summary>
public static class RowBoundScanner
{
    /// <summary>Finds the first value of <paramref name="record"/> above the bound.</summary>
    /// <param name="record">One record's JSON, exactly as the reader yielded it.</param>
    /// <param name="boundBytes">The most bytes of decoded UTF-8 a value may carry.</param>
    /// <returns>The first value over the bound, or <see langword="null"/> when every value is within it.</returns>
    public static RowBoundFinding? FindFirst(ReadOnlySpan<byte> record, int boundBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(boundBytes, 1);
        return Scan(record, boundBytes, imageBoundBytes: null);
    }

    /// <summary>
    /// Finds the first value of <paramref name="record"/> above the bound that applies to it:
    /// <paramref name="imageDataUriBoundBytes"/> for a value that is an image data URI
    /// (<see cref="ImageDataUri"/>), and <paramref name="boundBytes"/> for every other string.
    /// The finding names the bound that applied. A value is classified only once it is already
    /// above <paramref name="boundBytes"/>, so an ordinary record is still scanned with no decoding.
    /// </summary>
    /// <param name="record">One record's JSON, exactly as the reader yielded it.</param>
    /// <param name="boundBytes">The most bytes of decoded UTF-8 a value that is not an image data URI may carry.</param>
    /// <param name="imageDataUriBoundBytes">The most bytes of decoded UTF-8 an image data URI may carry.</param>
    /// <returns>The first value over its bound, or <see langword="null"/> when every value is within it.</returns>
    public static RowBoundFinding? FindFirst(ReadOnlySpan<byte> record, int boundBytes, int imageDataUriBoundBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(boundBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageDataUriBoundBytes, boundBytes);
        return Scan(record, boundBytes, imageDataUriBoundBytes);
    }

    private static RowBoundFinding? Scan(ReadOnlySpan<byte> record, int boundBytes, int? imageBoundBytes)
    {

        // One frame per level of the record, the record's own object included: the depth the
        // profile admits bounds the stack, so it never grows.
        Span<Frame> frames = stackalloc Frame[DataContractLimits.Depth + 1];
        var depth = -1;
        var reader = new Utf8JsonReader(record, new JsonReaderOptions { MaxDepth = DataContractLimits.Depth });
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    // The name is kept as its position in the record, and read only when a
                    // value under it is found: an ordinary record copies nothing.
                    frames[depth].NameStart = checked((int)reader.TokenStartIndex) + 1;
                    frames[depth].NameLength = reader.ValueSpan.Length;
                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (depth >= 0 && frames[depth].IsArray)
                    {
                        frames[depth].Index++;
                    }

                    depth++;
                    frames[depth] = new Frame
                    {
                        IsArray = reader.TokenType == JsonTokenType.StartArray,
                        Index = -1,
                    };
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    break;

                case JsonTokenType.String:
                    if (depth >= 0 && frames[depth].IsArray)
                    {
                        frames[depth].Index++;
                    }

                    var raw = reader.ValueSpan;
                    if (raw.Length > boundBytes)
                    {
                        var decoded = Utf8StringLength.Decoded(raw, reader.ValueIsEscaped);
                        if (decoded > boundBytes)
                        {
                            var applied = imageBoundBytes is { } imageBound && ImageDataUri.Is(raw, reader.ValueIsEscaped)
                                ? imageBound
                                : boundBytes;
                            if (decoded > applied)
                            {
                                return new RowBoundFinding(Pointer(frames[..(depth + 1)], record), decoded, applied);
                            }
                        }
                    }

                    break;

                default:
                    if (depth >= 0 && frames[depth].IsArray)
                    {
                        frames[depth].Index++;
                    }

                    break;
            }
        }

        return null;
    }

    // The pointer of the value being read: one segment per open frame, in the order the
    // record opens them - an array's position, or the member's name as it decodes.
    private static string Pointer(ReadOnlySpan<Frame> frames, ReadOnlySpan<byte> record)
    {
        var pointer = new StringBuilder();
        foreach (var frame in frames)
        {
            pointer.Append('/');
            if (frame.IsArray)
            {
                pointer.Append(frame.Index.ToString(CultureInfo.InvariantCulture));
            }
            else if (frame.NameLength > 0)
            {
                pointer.Append(Name(record.Slice(frame.NameStart, frame.NameLength)));
            }
        }

        return pointer.ToString();
    }

    // The member's name as the pointer carries it: its escapes resolved, then the pointer
    // grammar's own two characters escaped, so one name is always one segment.
    private static string Name(ReadOnlySpan<byte> raw)
    {
        var decoded = new byte[raw.Length];
        var written = Unescape(raw, decoded);
        return Encoding.UTF8.GetString(decoded, 0, written)
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
    }

    // Resolves the escapes of a JSON string's text into <paramref name="destination"/>, which
    // is never shorter than the text, and answers how many bytes were written.
    private static int Unescape(ReadOnlySpan<byte> raw, Span<byte> destination)
    {
        var written = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] != (byte)'\\' || index + 1 == raw.Length)
            {
                destination[written++] = raw[index];
                continue;
            }

            var escape = raw[++index];
            switch (escape)
            {
                case (byte)'"':
                case (byte)'\\':
                case (byte)'/':
                    destination[written++] = escape;
                    break;

                case (byte)'b':
                    destination[written++] = 0x08;
                    break;

                case (byte)'f':
                    destination[written++] = 0x0C;
                    break;

                case (byte)'n':
                    destination[written++] = 0x0A;
                    break;

                case (byte)'r':
                    destination[written++] = 0x0D;
                    break;

                case (byte)'t':
                    destination[written++] = 0x09;
                    break;

                case (byte)'u':
                    var code = Hex(raw.Slice(index + 1, 4));
                    index += 4;
                    if (code is >= 0xD800 and <= 0xDBFF
                        && index + 6 < raw.Length
                        && raw[index + 1] == (byte)'\\'
                        && raw[index + 2] == (byte)'u')
                    {
                        var low = Hex(raw.Slice(index + 3, 4));
                        if (low is >= 0xDC00 and <= 0xDFFF)
                        {
                            code = char.ConvertToUtf32((char)code, (char)low);
                            index += 6;
                        }
                    }

                    written += Encode(code, destination[written..]);
                    break;
            }
        }

        return written;
    }

    // One code point as UTF-8. A lone surrogate is not one: it becomes the replacement
    // character, which is what the name turns into once it is written out.
    private static int Encode(int scalar, Span<byte> destination)
    {
        if (scalar is >= 0xD800 and <= 0xDFFF)
        {
            scalar = 0xFFFD;
        }

        switch (scalar)
        {
            case < 0x80:
                destination[0] = (byte)scalar;
                return 1;

            case < 0x800:
                destination[0] = (byte)(0xC0 | (scalar >> 6));
                destination[1] = (byte)(0x80 | (scalar & 0x3F));
                return 2;

            case < 0x10000:
                destination[0] = (byte)(0xE0 | (scalar >> 12));
                destination[1] = (byte)(0x80 | ((scalar >> 6) & 0x3F));
                destination[2] = (byte)(0x80 | (scalar & 0x3F));
                return 3;

            default:
                destination[0] = (byte)(0xF0 | (scalar >> 18));
                destination[1] = (byte)(0x80 | ((scalar >> 12) & 0x3F));
                destination[2] = (byte)(0x80 | ((scalar >> 6) & 0x3F));
                destination[3] = (byte)(0x80 | (scalar & 0x3F));
                return 4;
        }
    }

    // The code unit of four hexadecimal digits. The text is a member name the reader
    // validated, so the digits are hexadecimal and the four are always there.
    private static int Hex(ReadOnlySpan<byte> digits)
    {
        var value = 0;
        foreach (var digit in digits)
        {
            value = (value << 4)
                | (digit <= (byte)'9' ? digit - (byte)'0' : (digit & 0xDF) - (byte)'A' + 10);
        }

        return value;
    }

    // One open container of the record: whether it is an array and the position of the value
    // being read in it, or the member that value sits under, as a range of the record's bytes.
    private struct Frame
    {
        public bool IsArray;
        public int Index;
        public int NameStart;
        public int NameLength;
    }
}
