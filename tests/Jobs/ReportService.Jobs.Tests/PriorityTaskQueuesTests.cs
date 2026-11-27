using ReportService.Jobs;

namespace ReportService.Jobs.Tests;

/// <summary>
/// The task queue of each priority class: the normal class keeps the kind's own queue, the
/// others add a suffix; a worker's served class is one of the three, absent meaning normal.
/// </summary>
public sealed class PriorityTaskQueuesTests
{
    private const string Render = "report-pdf-rendering";
    private const string Merge = "report-pdf-merge";

    [Theory]
    [InlineData(Render, JobPriority.High, "report-pdf-rendering-high")]
    [InlineData(Render, JobPriority.Normal, "report-pdf-rendering")]
    [InlineData(Render, JobPriority.Low, "report-pdf-rendering-low")]
    [InlineData(Merge, JobPriority.High, "report-pdf-merge-high")]
    [InlineData(Merge, JobPriority.Normal, "report-pdf-merge")]
    [InlineData(Merge, JobPriority.Low, "report-pdf-merge-low")]
    public void EachClassHasItsQueueAndNormalKeepsTheKindsOwn(string baseQueue, JobPriority priority, string expected) =>
        Assert.Equal(expected, PriorityTaskQueues.For(baseQueue, priority));

    [Fact]
    public void AClassOutsideTheThreeOrNoQueueIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorityTaskQueues.For(Render, (JobPriority)42));
        Assert.Throws<ArgumentException>(() => PriorityTaskQueues.For(" ", JobPriority.Normal));
        Assert.Throws<ArgumentNullException>(() => PriorityTaskQueues.For(null!, JobPriority.Normal));
    }

    [Fact]
    public void AllListsTheThreeQueuesFromTheHighestClass() =>
        Assert.Equal(
            ["report-pdf-merge-high", "report-pdf-merge", "report-pdf-merge-low"],
            PriorityTaskQueues.All(Merge));

    [Theory]
    [InlineData("report-pdf-rendering-high", JobPriority.High)]
    [InlineData("report-pdf-rendering", JobPriority.Normal)]
    [InlineData("report-pdf-merge-low", JobPriority.Low)]
    [InlineData("report-orchestration", JobPriority.Normal)]
    public void ClassOfReadsTheSuffix(string queue, JobPriority expected) =>
        Assert.Equal(expected, PriorityTaskQueues.ClassOf(queue));

    [Fact]
    public void ClassOfRoundTripsEveryClassQueue()
    {
        foreach (var priority in new[] { JobPriority.High, JobPriority.Normal, JobPriority.Low })
        {
            Assert.Equal(priority, PriorityTaskQueues.ClassOf(PriorityTaskQueues.For(Render, priority)));
        }

        Assert.Throws<ArgumentException>(() => PriorityTaskQueues.ClassOf(""));
    }

    [Theory]
    [InlineData(null, true, JobPriority.Normal)]
    [InlineData("", true, JobPriority.Normal)]
    [InlineData("   ", true, JobPriority.Normal)]
    [InlineData("high", true, JobPriority.High)]
    [InlineData("normal", true, JobPriority.Normal)]
    [InlineData("low", true, JobPriority.Low)]
    [InlineData("High", false, JobPriority.Normal)]
    [InlineData("urgent", false, JobPriority.Normal)]
    [InlineData("high,low", false, JobPriority.Normal)]
    [InlineData(" high", false, JobPriority.Normal)]
    public void AServedClassIsExactlyOneClassOrAbsent(string? text, bool accepted, JobPriority expected)
    {
        Assert.Equal(accepted, PriorityTaskQueues.TryParseServedClass(text, out var served));
        Assert.Equal(expected, served);
    }

    [Theory]
    [InlineData(null, "report-pdf-rendering")]
    [InlineData("high", "report-pdf-rendering-high")]
    [InlineData("low", "report-pdf-rendering-low")]
    [InlineData("high,low", "report-pdf-rendering")]
    public void TheServedQueueFollowsTheSettingAndARefusedValueFallsBackToNormal(string? setting, string expected) =>
        Assert.Equal(expected, PriorityTaskQueues.ServedQueue(Render, setting));
}
