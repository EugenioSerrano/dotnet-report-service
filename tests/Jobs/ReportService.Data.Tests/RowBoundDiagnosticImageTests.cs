using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The row bound's diagnostic for an image data URI: the same sentence as every row-bound
/// refusal, naming the image bound that applied.
/// </summary>
public sealed class RowBoundDiagnosticImageTests
{
    [Fact]
    public void TheReasonOfAnImageNamesTheImageBound()
    {
        Assert.Equal(
            "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=1048577 bound=1048576 limit=row-bound ordinal=3 offset=120 path=$.logo",
            RowBoundDiagnostic.For(3, 120, 1_048_577, PdfRowBound.ImageDataUriBytes, "$.logo"));
    }

    [Fact]
    public void TheImageBoundIsOneMebibyte()
    {
        Assert.Equal(1_048_576, PdfRowBound.ImageDataUriBytes);
    }
}
