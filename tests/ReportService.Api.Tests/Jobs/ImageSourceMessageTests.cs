using ReportService.Api.Jobs;
using ReportService.Testing;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The sentence a producer reads when an output refused the source of an image: the part, the
/// record of a row part and the attribute, read whole and in order from the stored reason, and
/// never a value. A reason this build cannot read renders no message and is reported once.
/// </summary>
public sealed class ImageSourceMessageTests
{
    private const string Code = "IMAGE_SOURCE_INVALID";

    [Fact]
    public void ARowRefusalNamesThePartTheRecordAndTheAttribute()
    {
        var warnings = new LogCapture();

        var message = ErrorMessages.Format(Code, "part=row ordinal=12 attribute=src", warnings.CreateLogger("api"));

        Assert.Equal(
            "The row part of record 12 writes an image whose src is not an image this output accepts. Give the image as a base64 data URI of a PNG, JPEG, GIF, WebP or SVG image; a remote reference is never printed, and a relative one is not printed in a PDF.",
            message);
        Assert.Empty(warnings.Lines);
    }

    [Fact]
    public void ADocumentPartRefusalNamesNoRecord()
    {
        var message = ErrorMessages.Format(Code, "part=document-start ordinal=0 attribute=xlink:href", new LogCapture().CreateLogger("api"));

        Assert.StartsWith("The document-start part writes an image whose xlink:href is not", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("part=row ordinal=12")]
    [InlineData("ordinal=12 part=row attribute=src")]
    [InlineData("part=body ordinal=1 attribute=src")]
    [InlineData("part=row ordinal=-1 attribute=src")]
    [InlineData("part=row ordinal=1 attribute=onclick")]
    [InlineData("part=row ordinal=1 attribute=src value=https://example.invalid")]
    [InlineData("https://example.invalid/x.png")]
    public void AReasonThisBuildDidNotWriteRendersNoMessage(string reason)
    {
        var warnings = new LogCapture();

        Assert.Null(ErrorMessages.Format(Code, reason, warnings.CreateLogger("api")));
        Assert.Single(warnings.Lines);
        Assert.DoesNotContain("example.invalid", warnings.Lines[0], StringComparison.Ordinal);
    }
}
