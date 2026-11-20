using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The reason a PDF output is refused by the row bound: the delivered diagnostic sentence of
/// every data refusal, carrying the record's ordinal, the field's path, its size and the
/// bound - and never a value of the document. The sentence is built by the delivered builder,
/// not by a copy of its grammar.
/// </summary>
public sealed class RowBoundDiagnosticTests
{
    [Fact]
    public void TheReasonIsTheDeliveredSentenceWithTheTwoNumbersTheBoundAdds()
    {
        Assert.Equal(
            "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description",
            RowBoundDiagnostic.For(1_500, 48_213, 262_145, PdfRowBound.Bytes, "$.description"));
    }

    [Fact]
    public void TheReasonIsWhatTheDeliveredBuilderProducesForTheSameFields()
    {
        var built = new DataContractViolation(
            DataContractViolation.RowBoundExceeded,
            "field-too-large size=262145 bound=262144",
            "row-bound",
            1_500,
            48_213,
            "$.description");

        Assert.Equal(built.Message, RowBoundDiagnostic.For(1_500, 48_213, 262_145, PdfRowBound.Bytes, "$.description"));
    }

    [Fact]
    public void TheCodeHasItsHomeBesideTheOtherRefusalsOfTheProfile()
    {
        Assert.Equal("ROW_BOUND_EXCEEDED", DataContractViolation.RowBoundExceeded);
        Assert.Equal(262_144, PdfRowBound.Bytes);
    }

    [Fact]
    public void AStepResultCarriesTheRefusalsCodeAndReasonAndIsAValueOfItsOwn()
    {
        var refusal = new PdfOutputRefusal(DataContractViolation.RowBoundExceeded, "the reason");
        var result = new PartitionResult(1, 1, 1, 24, 1, "block/job/0/attempt", 1, new string('a', 64), "index/job/0/attempt", 1, new string('b', 64), refusal);

        Assert.Same(refusal, result.PdfRefusal);
        Assert.Equal(new PdfOutputRefusal(DataContractViolation.RowBoundExceeded, "the reason"), refusal);
        Assert.Equal(refusal.GetHashCode(), new PdfOutputRefusal(DataContractViolation.RowBoundExceeded, "the reason").GetHashCode());
        Assert.Contains("ROW_BOUND_EXCEEDED", refusal.ToString(), StringComparison.Ordinal);
        Assert.Null(new PartitionResult(1, 1, 1, 24, 1, "block/job/0/attempt", 1, new string('a', 64), "index/job/0/attempt", 1, new string('b', 64)).PdfRefusal);
    }

    [Fact]
    public void TheReasonOfTheLongestRenderedPathStaysInsideItsBudget()
    {
        // The delivered renderer caps a rendered path at 1 024 bytes, including its ellipsis.
        // The sentence's own fixed part before the path is 148 bytes, so the longest reason
        // this version can write is 1 172 bytes - the measured worst case, never truncated.
        var path = "$." + new string('a', 1_022);

        var reason = RowBoundDiagnostic.For(1_500, 48_213, 262_145, PdfRowBound.Bytes, path);

        Assert.Equal(1_024, Encoding.UTF8.GetByteCount(path));
        Assert.Equal(1_172, Encoding.UTF8.GetByteCount(reason));
        Assert.EndsWith(" path=" + path, reason, StringComparison.Ordinal);
    }
}
