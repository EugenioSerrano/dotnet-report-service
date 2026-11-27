using ReportService.Jobs;

namespace ReportService.Jobs.Tests;

/// <summary>
/// The capacity judgement: sums exactly at the configured values fit, one page or one
/// byte more is refused naming the limit, the configured value and the reached value,
/// and pages are judged before bytes.
/// </summary>
public sealed class DocumentCapacityTests
{
    private static readonly DocumentCapacity Capacity = new(300, 4_096, "judge");

    [Fact]
    public void SumsExactlyAtTheConfiguredValuesFit() => Assert.Null(Capacity.Judge(300, 4_096));

    [Fact]
    public void OnePageMoreIsRefusedWithTheConfiguredValue()
    {
        var refusal = Capacity.Judge(301, 1);

        Assert.Equal(new CapacityRefusal("document-pages", 300, 301), refusal);
        Assert.Equal("limit=document-pages limitValue=300 reached=301", refusal!.Reason);
    }

    [Fact]
    public void OneByteMoreIsRefusedWithTheConfiguredValue() =>
        Assert.Equal(new CapacityRefusal("document-bytes", 4_096, 4_097), Capacity.Judge(1, 4_097));

    [Fact]
    public void PagesAreJudgedBeforeBytes() =>
        Assert.Equal("document-pages", Capacity.Judge(301, 4_097)!.Limit);

    [Fact]
    public void ACapacityOfOnePageRefusesTheSecondPage()
    {
        var one = new DocumentCapacity(1, long.MaxValue, "one");

        Assert.Null(one.Judge(1, 0));
        Assert.Equal(2, one.Judge(2, 0)!.ReachedValue);
    }
}
