using System.Text;
using System.Text.Json;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The decoded UTF-8 length of a JSON string's raw span: the span's own length when the value
/// carries no escape, and one forward walk when it does - each escape counted as the bytes it
/// becomes once decoded, a surrogate pair as four, a lone surrogate as three. It never
/// allocates and never decodes a value, so a value the reader admits but cannot copy is
/// measured without throwing.
/// </summary>
public sealed class Utf8StringLengthTests
{
    [Fact]
    public void AnUnescapedValueIsTheLengthOfItsOwnSpan()
    {
        var raw = new byte[262_145];
        Array.Fill(raw, (byte)'a');

        Assert.Equal(262_145, Utf8StringLength.Decoded(raw, isEscaped: false));
    }

    [Theory]
    [InlineData("a\\nb", 3)]
    [InlineData("\\t", 1)]
    [InlineData("\\\"", 1)]
    [InlineData("\\\\", 1)]
    [InlineData("\\/", 1)]
    [InlineData("\\b", 1)]
    [InlineData("\\f", 1)]
    [InlineData("\\r", 1)]
    [InlineData("\\u0041", 1)]
    [InlineData("\\u00E9", 2)]
    [InlineData("\\u20AC", 3)]
    [InlineData("\\uD83D\\uDE00", 4)]
    public void EveryEscapeCountsTheBytesItBecomes(string raw, int decoded)
    {
        Assert.Equal(decoded, Utf8StringLength.Decoded(Encoding.UTF8.GetBytes(raw), isEscaped: true));
    }

    [Fact]
    public void ALoneSurrogateCountsThreeBytesAndNothingThrows()
    {
        // The reader admits the escape and CopyString refuses to decode it; the counter
        // measures what the value becomes once replaced or encoded - three bytes - and
        // never decodes it.
        Assert.Equal(3, Utf8StringLength.Decoded("\\uD800"u8, isEscaped: true));
        Assert.Equal(3, Utf8StringLength.Decoded("\\uDC00"u8, isEscaped: true));
    }

    [Fact]
    public void AnEscapedValueOverTheBoundRawAndUnderItDecodedCountsTheDecodedLength()
    {
        // 52 429 escaped e-acute characters: 314 574 bytes as written, 104 858 decoded.
        var raw = string.Concat(Enumerable.Repeat("\\u00E9", 52_429));

        Assert.Equal(104_858, Utf8StringLength.Decoded(Encoding.UTF8.GetBytes(raw), isEscaped: true));
    }

    [Fact]
    public void ASpanBuiltByHandIsCountedWithoutThrowing()
    {
        // The counter is given the reader's own spans; a span built by hand still never
        // throws on its content, and counts the character each escape names.
        Assert.Equal(3, Utf8StringLength.Decoded("\\u00"u8, isEscaped: true));
        Assert.Equal(2, Utf8StringLength.Decoded("\\u00e9"u8, isEscaped: true));
        Assert.Equal(3, Utf8StringLength.Decoded("\\uZZZZ"u8, isEscaped: true));
        Assert.Equal(1, Utf8StringLength.Decoded("\\q"u8, isEscaped: true));
    }

    [Fact]
    public void TheCounterAgreesWithTheReadersOwnDecodingOfTwoThousandStrings()
    {
        // Every kind of content a valid JSON string can carry: plain bytes of every UTF-8
        // width, every short escape, the escapes of the three widths and a surrogate pair.
        // A lone surrogate is not here: the reader admits it and refuses to copy it, so it has
        // a case of its own rather than an agreement this cannot check.
        (string Raw, int Decoded)[] pieces =
        [
            ("a", 1),
            ("Z", 1),
            ("0", 1),
            (" ", 1),
            ("\u00E9", 2),
            ("\u20AC", 3),
            ("\U0001F600", 4),
            ("\\n", 1),
            ("\\t", 1),
            ("\\\"", 1),
            ("\\\\", 1),
            ("\\/", 1),
            ("\\b", 1),
            ("\\f", 1),
            ("\\r", 1),
            ("\\u0041", 1),
            ("\\u00E9", 2),
            ("\\u20AC", 3),
            ("\\uD83D\\uDE00", 4),
        ];

        var random = new Random(20261119);
        for (var index = 0; index < 2_000; index++)
        {
            var builder = new StringBuilder("\"");
            var expected = 0;
            for (var piece = random.Next(1, 12); piece > 0; piece--)
            {
                var (raw, decoded) = pieces[random.Next(pieces.Length)];
                builder.Append(raw);
                expected += decoded;
            }

            builder.Append('"');
            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            var reader = new Utf8JsonReader(bytes);
            Assert.True(reader.Read());

            var copied = reader.CopyString(new byte[bytes.Length]);
            Assert.Equal(expected, copied);
            Assert.Equal(copied, Utf8StringLength.Decoded(reader.ValueSpan, reader.ValueIsEscaped));
        }
    }
}
