using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The pure aging rule: only a low output with a wait samples; pending samples accrue a period;
/// the job is promoted at the wait, once; an output adopts a promotion another made; the sample
/// period is a twentieth of the wait within its bounds; and the sample cap drains the window
/// before a continue-as-new, never with renders outstanding.
/// </summary>
public sealed class PriorityAgingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(JobPriority.Normal)]
    [InlineData(JobPriority.High)]
    public void AnOutputThatIsNotLowNeverSamples(JobPriority priority)
    {
        Assert.False(PriorityAging.Samples(priority, Wait));
        Assert.Equal(PriorityAgingAction.Stop, PriorityAging.Decide(priority, Wait, 0, true, JobPriority.Low, true).Action);
    }

    [Fact]
    public void AnOutputWithoutAWaitNeverSamples()
    {
        Assert.False(PriorityAging.Samples(JobPriority.Low, null));
        Assert.False(PriorityAging.Samples(JobPriority.Low, TimeSpan.Zero));
        Assert.Equal(PriorityAgingAction.Stop, PriorityAging.Decide(JobPriority.Low, null, 0, true, JobPriority.Low, true).Action);
    }

    [Fact]
    public void AnOutputWithNoWorkLeftStopsSampling() =>
        Assert.Equal(PriorityAgingAction.Stop, PriorityAging.Decide(JobPriority.Low, Wait, 0, true, JobPriority.Low, workRemaining: false).Action);

    [Fact]
    public void APendingSampleAccruesOnePeriodAndANonPendingOneNothing()
    {
        var pending = PriorityAging.Decide(JobPriority.Low, Wait, 60, samplePending: true, JobPriority.Low, true);
        Assert.Equal(PriorityAgingAction.SampleAgain, pending.Action);
        Assert.Equal(90, pending.PendingSeconds);

        var running = PriorityAging.Decide(JobPriority.Low, Wait, 60, samplePending: false, JobPriority.Low, true);
        Assert.Equal(PriorityAgingAction.SampleAgain, running.Action);
        Assert.Equal(60, running.PendingSeconds);
    }

    [Fact]
    public void TheJobIsPromotedExactlyWhenThePendingTimeReachesTheWait()
    {
        Assert.Equal(PriorityAgingAction.SampleAgain, PriorityAging.Decide(JobPriority.Low, Wait, 540, true, JobPriority.Low, true).Action);
        var promoted = PriorityAging.Decide(JobPriority.Low, Wait, 570, true, JobPriority.Low, true);
        Assert.Equal(PriorityAgingAction.Promote, promoted.Action);
        Assert.Equal(600, promoted.PendingSeconds);
    }

    [Fact]
    public void APromotedOutputIsNormalAndIsNeverPromotedAgain() =>
        Assert.Equal(PriorityAgingAction.Stop, PriorityAging.Decide(JobPriority.Normal, Wait, 600, true, JobPriority.Normal, true).Action);

    [Theory]
    [InlineData(JobPriority.Normal)]
    [InlineData(JobPriority.High)]
    public void AnOutputAdoptsAClassTheStoreAlreadyMoved(JobPriority stored) =>
        Assert.Equal(PriorityAgingAction.Adopt, PriorityAging.Decide(JobPriority.Low, Wait, 0, true, stored, true).Action);

    [Fact]
    public void AJobTheStoreDoesNotFindKeepsSampling() =>
        Assert.Equal(PriorityAgingAction.SampleAgain, PriorityAging.Decide(JobPriority.Low, Wait, 0, true, null, true).Action);

    [Fact]
    public void ANegativePendingTimeIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorityAging.Decide(JobPriority.Low, Wait, -1, true, JobPriority.Low, true));

    [Theory]
    [InlineData(600, 30)]
    [InlineData(20, 5)]
    [InlineData(5, 5)]
    [InlineData(7200, 60)]
    [InlineData(200, 10)]
    public void TheSamplePeriodIsATwentiethOfTheWaitBetweenFiveAndSixtySeconds(int waitSeconds, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PriorityAging.SamplePeriod(TimeSpan.FromSeconds(waitSeconds)));

    [Fact]
    public void ANonPositiveWaitHasNoPeriod() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorityAging.SamplePeriod(TimeSpan.Zero));

    [Fact]
    public void BelowTheCapTheWindowDecidesAlone()
    {
        var schedule = new PdfOutputWindowDecision(PdfOutputWindowAction.Schedule, 3);
        Assert.Same(schedule, PriorityAging.Window(schedule, PriorityAging.SamplesBeforeContinueAsNew - 1, 1, 3, 10));
    }

    [Fact]
    public void AtTheCapTheRunDrainsBeforeItContinuesAsNew()
    {
        var schedule = new PdfOutputWindowDecision(PdfOutputWindowAction.Schedule, 3);
        Assert.Equal(PdfOutputWindowAction.Wait, PriorityAging.Window(schedule, PriorityAging.SamplesBeforeContinueAsNew, 2, 3, 10).Action);
        Assert.Equal(PdfOutputWindowAction.ContinueAsNew, PriorityAging.Window(schedule, PriorityAging.SamplesBeforeContinueAsNew, 0, 3, 10).Action);
    }

    [Fact]
    public void AtTheCapWithEveryBlockScheduledTheWindowDecidesAlone()
    {
        var wait = new PdfOutputWindowDecision(PdfOutputWindowAction.Wait, null);
        Assert.Same(wait, PriorityAging.Window(wait, PriorityAging.SamplesBeforeContinueAsNew + 5, 1, 10, 10));
    }

    [Fact]
    public void TheCapNeverContinuesAsNewWithRendersOutstanding()
    {
        for (var outstanding = 1; outstanding <= 64; outstanding++)
        {
            var decided = PriorityAging.Window(
                new PdfOutputWindowDecision(PdfOutputWindowAction.Schedule, 5),
                PriorityAging.SamplesBeforeContinueAsNew,
                outstanding,
                5,
                100);
            Assert.NotEqual(PdfOutputWindowAction.ContinueAsNew, decided.Action);
        }
    }

    [Fact]
    public void TheWindowWrapperValidatesItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => PriorityAging.Window(null!, 0, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorityAging.Window(new PdfOutputWindowDecision(PdfOutputWindowAction.Complete, null), -1, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorityAging.Window(new PdfOutputWindowDecision(PdfOutputWindowAction.Complete, null), 0, -1, 0, 1));
    }

    [Theory]
    [InlineData("00:00:05", true)]
    [InlineData("00:10:00", true)]
    [InlineData("7.00:00:00", true)]
    [InlineData("00:00:04", false)]
    [InlineData("7.00:00:01", false)]
    public void TheAgingWaitIsBetweenFiveSecondsAndSevenDays(string wait, bool valid)
    {
        var options = new PriorityAgingOptions { Wait = TimeSpan.Parse(wait, System.Globalization.CultureInfo.InvariantCulture) };
        var problems = options.Validate().ToList();
        Assert.Equal(valid, problems.Count == 0);
        Assert.All(problems, problem => Assert.Equal(PriorityAgingOptions.WaitSetting, problem.Setting));
    }

    [Fact]
    public void TheDefaultAgingWaitIsTenMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(10), new PriorityAgingOptions().Wait);

    [Fact]
    public void EveryClassStepOptionKeepsEveryValueButTheQueue()
    {
        foreach (var priority in new[] { JobPriority.High, JobPriority.Normal, JobPriority.Low })
        {
            AssertSameButQueue(RenderPdfBlockStepOptions.Create(), RenderPdfBlockStepOptions.Create(priority), PriorityTaskQueues.For("report-pdf-rendering", priority));
            AssertSameButQueue(AssemblePdfStepOptions.Create(), AssemblePdfStepOptions.Create(priority), PriorityTaskQueues.For("report-pdf-merge", priority));
            AssertSameButQueue(TakeTemplateStepOptions.Create(), TakeTemplateStepOptions.Create(priority), PriorityTaskQueues.For("report-pdf-rendering", priority));
            AssertSameButQueue(ResolveTemplateStepOptions.Create(), ResolveTemplateStepOptions.Create(priority), PriorityTaskQueues.For("report-pdf-rendering", priority));
        }

        Assert.Equal("report-pdf-rendering", RenderPdfBlockStepOptions.Create().TaskQueue);
        Assert.Equal("report-pdf-merge", AssemblePdfStepOptions.Create().TaskQueue);
    }

    private static void AssertSameButQueue(Temporalio.Workflows.ActivityOptions expected, Temporalio.Workflows.ActivityOptions actual, string queue)
    {
        Assert.Equal(queue, actual.TaskQueue);
        Assert.Equal(expected.StartToCloseTimeout, actual.StartToCloseTimeout);
        Assert.Equal(expected.HeartbeatTimeout, actual.HeartbeatTimeout);
        Assert.Equal(expected.CancellationType, actual.CancellationType);
        Assert.Equal(expected.RetryPolicy!.MaximumAttempts, actual.RetryPolicy!.MaximumAttempts);
        Assert.Equal(expected.RetryPolicy.InitialInterval, actual.RetryPolicy.InitialInterval);
        Assert.Equal(expected.RetryPolicy.NonRetryableErrorTypes, actual.RetryPolicy.NonRetryableErrorTypes);
    }
}
