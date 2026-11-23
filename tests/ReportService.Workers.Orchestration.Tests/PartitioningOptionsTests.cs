using ReportService.Data;
using ReportService.Workers.Orchestration;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The temporary single-block setting: required and at least one, refused at start by
/// its name and never its value, valid at and above the block's hard maximum, and the
/// one start-up line that says which value governs — with the ineffective branch.
/// </summary>
public sealed class PartitioningOptionsTests
{
    [Fact]
    public void AConfiguredCountIsValid()
    {
        Assert.Empty(new PartitioningOptions { SingleBlockRecordLimit = 1_000 }.Validate());
    }

    [Fact]
    public void AMissingValueIsRefusedByName()
    {
        var problem = Assert.Single(new PartitioningOptions().Validate());

        Assert.Equal(PartitioningOptions.SingleBlockRecordLimitSetting, problem.Setting);
        Assert.Contains("greater than zero", problem.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("0", problem.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveValueIsRefusedByNameAndNeverByValue(int value)
    {
        var options = new PartitioningOptions { SingleBlockRecordLimit = value };

        var problem = Assert.Single(options.Validate());

        Assert.Equal(PartitioningOptions.SingleBlockRecordLimitSetting, problem.Setting);
        Assert.DoesNotContain(value.ToString(System.Globalization.CultureInfo.InvariantCulture), problem.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(5_000)]
    public void AValueAtOrAboveTheHardMaximumIsValid(int value)
    {
        Assert.Empty(new PartitioningOptions { SingleBlockRecordLimit = value }.Validate());
    }

    [Fact]
    public void TheStartupLineNamesTheEffectiveLimitAndTheByteBound()
    {
        Assert.Equal(
            "single-block limit effective: records=1000 bytes=1048576",
            new SingleBlockLimit(1_000).DescribeStartupLine());
    }

    [Fact]
    public void TheStartupLineReportsAConfiguredValueThatHasNoEffect()
    {
        Assert.Equal(
            "single-block limit effective: records=2000 bytes=1048576 configured=5000 is above the hard maximum and has no effect",
            new SingleBlockLimit(5_000).DescribeStartupLine());
    }
}
