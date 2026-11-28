using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The PDF row bound by value class: a value that is an image data URI is bounded at
/// 1 048 576 bytes, every other string value at 262 144 - including a value of another media
/// type, a percent-encoded data URI and a value that starts like an image data URI but whose
/// body breaks the rule - and the finding names the bound that applied.
/// </summary>
public sealed class RowBoundImageClassTests
{
    private const int Ordinary = 262_144;
    private const int Image = 1_048_576;

    public static TheoryData<string, string> OrdinaryValues => new()
    {
        { "plain text", "text" },
        { "another media type", "data:application/pdf;base64," },
        { "an image type not listed", "data:image/bmp;base64," },
        { "percent-encoded", "data:image/png," },
        { "whitespace in the body", "data:image/png;base64, " },
        { "a character outside the alphabet", "data:image/png;base64,-" },
        { "too much padding", "data:image/png;base64,A===" },
        { "padding inside the body", "data:image/png;base64,A=" },
    };

    [Fact]
    public void AnImageAtItsBoundIsWithinItAndOneByteMoreIsFoundWithTheImageBound()
    {
        Assert.Null(Find(Record("logo", Quoted(ImageOf(Image)))));
        Assert.Equal(new RowBoundFinding("/logo", Image + 1, Image), Find(Record("logo", Quoted(ImageOf(Image + 1)))));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("image/svg+xml")]
    [InlineData("IMAGE/PNG")]
    public void EachListedMediaTypeIsBoundedAtTheImageBound(string mediaType)
    {
        Assert.Null(Find(Record("logo", Quoted(ImageOf(Image, mediaType)))));
        Assert.Equal(Image, Find(Record("logo", Quoted(ImageOf(Image + 1, mediaType))))?.BoundBytes);
    }

    [Fact]
    public void AnImageBetweenTheTwoBoundsIsAdmitted()
    {
        Assert.Null(Find(Record("logo", Quoted(ImageOf(Ordinary + 1)))));
        Assert.Null(Find(Record("logo", Quoted(ImageOf(700_000)))));
    }

    [Fact]
    public void AnImageFarAboveItsBoundIsStillNamedWithTheImageBound()
    {
        Assert.Equal(new RowBoundFinding("/logo", 1_500_000, Image), Find(Record("logo", Quoted(ImageOf(1_500_000)))));
    }

    [Fact]
    public void AnImageWrittenWithEscapesIsMeasuredAndClassifiedDecoded()
    {
        // Every solidus written as an escape: more bytes as written than decoded, the same class.
        var decoded = ImageOf(Image, slashEvery: 64);
        var written = decoded.Replace("/", "\\/", StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(written) > Image);

        Assert.Null(Find(Record("logo", Quoted(written))));
    }

    [Theory]
    [MemberData(nameof(OrdinaryValues))]
    public void EveryOtherValueKeepsTheOrdinaryBound(string label, string prefix)
    {
        Assert.False(string.IsNullOrEmpty(label));
        Assert.Null(Find(Record("v", Quoted(Sized(prefix, Ordinary)))));
        Assert.Equal(new RowBoundFinding("/v", Ordinary + 1, Ordinary), Find(Record("v", Quoted(Sized(prefix, Ordinary + 1)))));
    }

    [Fact]
    public void ABrokenBodyAboveTheImageBoundIsNamedWithTheOrdinaryBound()
    {
        var broken = ImageOf(Image) + " ";

        Assert.Equal(new RowBoundFinding("/v", Image + 1, Ordinary), Find(Record("v", Quoted(broken))));
    }

    [Fact]
    public void TheFirstValueOverItsBoundInDocumentOrderIsTheOneFound()
    {
        var record = "{\"logo\":" + Quoted(ImageOf(Image)) + ",\"text\":" + Quoted(new string('a', Ordinary + 1)) + "}";

        Assert.Equal(new RowBoundFinding("/text", Ordinary + 1, Ordinary), Find(record));
    }

    [Fact]
    public void AnImageNestedInAnArrayIsNamedByItsPointer()
    {
        var record = "{\"pictures\":[\"x\"," + Quoted(ImageOf(Image + 1)) + "]}";

        Assert.Equal(new RowBoundFinding("/pictures/1", Image + 1, Image), Find(record));
    }

    [Fact]
    public void TheSingleBoundScanStillAppliesOneBoundToEveryValue()
    {
        var bytes = Encoding.UTF8.GetBytes(Record("logo", Quoted(ImageOf(Ordinary + 1))));

        Assert.Equal(new RowBoundFinding("/logo", Ordinary + 1, Ordinary), RowBoundScanner.FindFirst(bytes, Ordinary));
    }

    // An image data URI of exactly the given byte length: the prefix carries a parameter whose
    // length makes the body's length a multiple of four.
    private static string ImageOf(int bytes, string mediaType = "image/png", int slashEvery = 0)
    {
        for (var pad = 1; pad <= 4; pad++)
        {
            var prefix = "data:" + mediaType + ";n=" + new string('p', pad) + ";base64,";
            var length = bytes - prefix.Length;
            if (length % 4 == 0)
            {
                var text = new StringBuilder(prefix, bytes);
                for (var index = 0; index < length; index++)
                {
                    text.Append(slashEvery > 0 && index % slashEvery == slashEvery - 1 ? '/' : 'A');
                }

                return text.ToString();
            }
        }

        throw new InvalidOperationException("four consecutive prefix lengths cover every remainder");
    }

    private static string Sized(string prefix, int bytes) => prefix + new string('a', bytes - prefix.Length);

    private static RowBoundFinding? Find(string record) => RowBoundScanner.FindFirst(Encoding.UTF8.GetBytes(record), PdfRowBound.Bytes, PdfRowBound.ImageDataUriBytes);

    private static string Record(string name, string value) => "{\"" + name + "\":" + value + "}";

    private static string Quoted(string value) => "\"" + value + "\"";
}
