using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The row bound of one detail record: the first string value, in document order, whose
/// decoded UTF-8 length is above the bound, named by the JSON Pointer of the field and its
/// decoded size. Only values are measured and never a property name, only a value written
/// with escapes is counted, and a value within the bound is settled by the length of its own
/// span, so the scan of an ordinary record allocates nothing.
/// </summary>
public sealed class RowBoundScannerTests
{
    [Fact]
    public void AValueAtTheBoundIsWithinItAndOneByteMoreIsFound()
    {
        Assert.Null(Find(Record("description", Quoted(new string('a', 262_144)))));
        Assert.Equal(new RowBoundFinding("/description", 262_145, 262_144), Find(Record("description", Quoted(new string('a', 262_145)))));
    }

    [Fact]
    public void TheBoundIsMeasuredInBytesOfTheDecodedValueAcrossEveryCharacterWidth()
    {
        // Two-byte characters: 131 072 e-acute are exactly the bound; one ASCII byte more
        // crosses it.
        Assert.Null(Find(Record("description", Quoted(new string('\u00E9', 131_072)))));
        Assert.Equal(
            new RowBoundFinding("/description", 262_145, 262_144),
            Find(Record("description", Quoted(new string('\u00E9', 131_072) + "a"))));

        // Three-byte characters: 87 381 euro signs and one ASCII byte are exactly the bound.
        Assert.Null(Find(Record("description", Quoted(new string('\u20AC', 87_381) + "a"))));
        Assert.Equal(
            new RowBoundFinding("/description", 262_145, 262_144),
            Find(Record("description", Quoted(new string('\u20AC', 87_381) + "ab"))));

        // Four-byte characters: 65 536 emoji are exactly the bound.
        Assert.Null(Find(Record("description", Quoted(Emoji(65_536)))));
        Assert.Equal(
            new RowBoundFinding("/description", 262_145, 262_144),
            Find(Record("description", Quoted(Emoji(65_536) + "a"))));
    }

    [Fact]
    public void AFieldOfOneHundredThousandThreeByteCharactersIsMeasuredInBytes()
    {
        var finding = Find(Record("description", Quoted(new string('\u20AC', 100_000))));

        Assert.Equal(new RowBoundFinding("/description", 300_000, 262_144), finding);
    }

    [Fact]
    public void AnEscapedValueOverTheBoundRawButUnderItDecodedIsNotRefused()
    {
        // 52 429 escaped e-acute characters are 314 574 bytes as written and 104 858 decoded.
        var escaped = string.Concat(Enumerable.Repeat("\\u00E9", 52_429));

        Assert.Null(Find(Record("description", "\"" + escaped + "\"")));
    }

    [Fact]
    public void AnEscapedValueOfExactlyTheBoundPlusOneDecodedIsFound()
    {
        var escaped = string.Concat(Enumerable.Repeat("\\u00E9", 131_072)) + "\\n";

        Assert.Equal(new RowBoundFinding("/description", 262_145, 262_144), Find(Record("description", "\"" + escaped + "\"")));
    }

    [Fact]
    public void ASurrogatePairCountsFourBytesAndALoneSurrogateThree()
    {
        // 65 536 escaped emoji are exactly the bound; one ASCII byte more crosses it.
        var emoji = string.Concat(Enumerable.Repeat("\\uD83D\\uDE00", 65_536));
        Assert.Null(Find(Record("description", "\"" + emoji + "\"")));
        Assert.Equal(new RowBoundFinding("/description", 262_145, 262_144), Find(Record("description", "\"" + emoji + "a\"")));

        // A lone surrogate is three bytes, and nothing throws while it is measured.
        var lone = string.Concat(Enumerable.Repeat("\\uD800", 87_382));
        Assert.Equal(new RowBoundFinding("/description", 262_146, 262_144), Find(Record("description", "\"" + lone + "\"")));
    }

    [Fact]
    public void ANestedFieldIsNamedByItsPointerFromTheRecord()
    {
        Assert.Equal(
            new RowBoundFinding("/lines/0/text", 262_145, 262_144),
            Find("""{"lines":[{"text":""" + Quoted(new string('a', 262_145)) + "}]}"));
    }

    [Fact]
    public void AValueOfAnArrayOfStringsIsNamedByItsPosition()
    {
        Assert.Equal(
            new RowBoundFinding("/tags/1", 262_145, 262_144),
            Find("""{"tags":["short",""" + Quoted(new string('a', 262_145)) + "]}"));
    }

    [Theory]
    [InlineData("a~b", "/a~0b")]
    [InlineData("a/b", "/a~1b")]
    public void AMemberNameIsEscapedAsThePointerGrammarRequires(string member, string pointer)
    {
        Assert.Equal(
            new RowBoundFinding(pointer, 262_145, 262_144),
            Find(Record(member, Quoted(new string('a', 262_145)))));
    }

    [Fact]
    public void AnEscapedMemberNameIsDecodedInThePointer()
    {
        Assert.Equal(
            new RowBoundFinding("/description", 262_145, 262_144),
            Find(Record("\\u0064escription", Quoted(new string('a', 262_145)))));
    }

    [Fact]
    public void AMemberNameIsDecodedWithEveryShortEscape()
    {
        // A newline, a tab, a quote, a backslash, a slash, a backspace, a form feed and a
        // carriage return in one name; the pointer escapes nothing but the slash.
        var finding = Find(Record("\\n\\t\\\"\\\\\\/\\b\\f\\r", Quoted(new string('a', 262_145))));

        Assert.Equal("/\n\t\"\\~1\b\f\r", finding!.Value.JsonPointer);
    }

    [Theory]
    [InlineData("\\u0041", "/A")]
    [InlineData("\\u00e9", "/\u00E9")]
    [InlineData("\\u00E9", "/\u00E9")]
    [InlineData("\\u20AC", "/\u20AC")]
    [InlineData("\\uD83D\\uDE00", "/\U0001F600")]
    [InlineData("\\uD800", "/\uFFFD")]
    public void AMemberNameIsDecodedFromItsUnicodeEscapesOfEveryWidth(string name, string pointer)
    {
        Assert.Equal(new RowBoundFinding(pointer, 262_145, 262_144), Find(Record(name, Quoted(new string('a', 262_145)))));
    }

    [Fact]
    public void APositionInAnArrayIsCountedAcrossValuesOfEveryKind()
    {
        var record = "{\"n\":[1,true,null," + Record("x", Quoted(new string('a', 262_145))) + "]}";

        Assert.Equal(new RowBoundFinding("/n/3/x", 262_145, 262_144), Find(record));
    }

    [Fact]
    public void ABoundBelowOneIsRefusedBeforeTheRecordIsRead()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RowBoundScanner.FindFirst("""{"a":"b"}"""u8.ToArray(), 0));
    }

    [Fact]
    public void AValueAtTheDepthLimitIsNamedByAllItsSegments()
    {
        const int Depth = DataContractLimits.Depth;
        var record = new StringBuilder();
        for (var level = 0; level < Depth; level++)
        {
            record.Append("""{"a":""");
        }

        record.Append(Quoted(new string('a', 262_145)));
        for (var level = 0; level < Depth; level++)
        {
            record.Append('}');
        }

        var finding = Find(record.ToString());

        Assert.NotNull(finding);
        Assert.Equal(Depth, finding!.Value.JsonPointer.Split('/').Length - 1);
        Assert.StartsWith("/a/a/", finding.Value.JsonPointer, StringComparison.Ordinal);
        Assert.EndsWith("/a/a", finding.Value.JsonPointer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstValueInDocumentOrderIsTheOneFound()
    {
        var record = new StringBuilder("""{"first":""")
            .Append(Quoted(new string('a', 262_145)))
            .Append(""","second":""")
            .Append(Quoted(new string('b', 262_146)))
            .Append('}')
            .ToString();

        Assert.Equal(new RowBoundFinding("/first", 262_145, 262_144), Find(record));
    }

    [Fact]
    public void APropertyNameIsNeverMeasured()
    {
        // The name is 300 000 bytes and the value is short: a name is not a field.
        Assert.Null(Find(Record(new string('n', 300_000), Quoted("short"))));
    }

    [Fact]
    public void NumbersBooleansNullsAndAnEmptyObjectCarryNoFinding()
    {
        Assert.Null(Find("""{"number":1,"flag":true,"none":null,"text":"short"}"""));
        Assert.Null(Find("{}"));
    }

    [Fact]
    public void AnOrdinaryRecordOfOneMebibyteIsScannedWithoutAllocating()
    {
        // Ten unescaped values of 100 000 bytes each: one mebibyte of record content, every
        // value within the bound. The scan is warmed up once and then measured.
        var record = new StringBuilder("{");
        for (var index = 0; index < 10; index++)
        {
            if (index > 0)
            {
                record.Append(',');
            }

            record.Append('"').Append('n').Append(index).Append("\":").Append(Quoted(new string((char)('a' + index), 100_000)));
        }

        record.Append('}');
        var bytes = Encoding.UTF8.GetBytes(record.ToString());
        Assert.Null(RowBoundScanner.FindFirst(bytes, PdfRowBound.Bytes, PdfRowBound.ImageDataUriBytes));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var finding = RowBoundScanner.FindFirst(bytes, PdfRowBound.Bytes, PdfRowBound.ImageDataUriBytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(finding);
        Assert.Equal(0, allocated);
    }

    private static RowBoundFinding? Find(string record) =>
        RowBoundScanner.FindFirst(Encoding.UTF8.GetBytes(record), PdfRowBound.Bytes, PdfRowBound.ImageDataUriBytes);

    private static string Record(string name, string value) => "{\"" + name + "\":" + value + "}";

    private static string Quoted(string value) => "\"" + value + "\"";

    private static string Emoji(int count) => string.Concat(Enumerable.Repeat("\U0001F600", count));
}
