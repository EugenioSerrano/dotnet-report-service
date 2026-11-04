using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Api.Enums.V1;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The two settings of the wait between two checks of a job's upload and the
/// arithmetic that bounds how many checks one run of the workflow may take. Each
/// setting is required and bounded, a refusal names the setting and never the value,
/// and the ceiling of the pass limit is derived from the history budget instead of
/// being written down, so a re-measurement moves it and no case of this file has to
/// change.
/// </summary>
public sealed class InputWaitOptionsTests
{
    [Fact]
    public void TheDefaultsAreHalfAMinuteBetweenChecksAndOneHundredChecks()
    {
        var options = new InputWaitOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), options.Backstop);
        Assert.Equal(100, options.ContinueAsNewAfter);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(300)]
    public void ABackstopInsideItsBoundsIsAccepted(int seconds)
    {
        var options = new InputWaitOptions { Backstop = TimeSpan.FromSeconds(seconds) };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void TheBackstopBoundsAreInclusiveAtBothEnds()
    {
        Assert.Empty(new InputWaitOptions { Backstop = InputWaitOptions.MinimumBackstop }.Validate());
        Assert.Empty(new InputWaitOptions { Backstop = InputWaitOptions.MaximumBackstop }.Validate());
    }

    [Fact]
    public void ABackstopOutsideItsBoundsIsRefusedByName()
    {
        foreach (var backstop in new[]
                 {
                     TimeSpan.Zero,
                     TimeSpan.FromTicks(-1),
                     InputWaitOptions.MinimumBackstop - TimeSpan.FromTicks(1),
                     InputWaitOptions.MaximumBackstop + TimeSpan.FromTicks(1),
                 })
        {
            var problem = Assert.Single(new InputWaitOptions { Backstop = backstop }.Validate());

            Assert.Equal(InputWaitOptions.BackstopSetting, problem.Setting);
            Assert.DoesNotContain(
                backstop.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                problem.Reason,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    public void APassLimitInsideItsBoundsIsAccepted(int passes)
    {
        var options = new InputWaitOptions { ContinueAsNewAfter = passes };

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APassLimitBelowOneIsRefusedByName(int passes)
    {
        var problem = Assert.Single(new InputWaitOptions { ContinueAsNewAfter = passes }.Validate());

        Assert.Equal(InputWaitOptions.ContinueAsNewAfterSetting, problem.Setting);
        Assert.DoesNotContain(passes.ToString(System.Globalization.CultureInfo.InvariantCulture), problem.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDerivedMaximumIsAcceptedAndOneCheckAboveItIsRefused()
    {
        var atTheCeiling = new InputWaitOptions { ContinueAsNewAfter = InputWaitHistoryBudget.MaxPasses };
        var aboveTheCeiling = new InputWaitOptions { ContinueAsNewAfter = InputWaitHistoryBudget.MaxPasses + 1 };

        Assert.Empty(atTheCeiling.Validate());
        var problem = Assert.Single(aboveTheCeiling.Validate());
        Assert.Equal(InputWaitOptions.ContinueAsNewAfterSetting, problem.Setting);
        Assert.DoesNotContain(
            aboveTheCeiling.ContinueAsNewAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
            problem.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRefusedSettingIsReportedTogether()
    {
        var problems = new InputWaitOptions { Backstop = TimeSpan.Zero, ContinueAsNewAfter = 0 }.Validate().ToList();

        Assert.Equal(2, problems.Count);
        Assert.Equal([InputWaitOptions.BackstopSetting, InputWaitOptions.ContinueAsNewAfterSetting], problems.ConvertAll(problem => problem.Setting));
    }

    [Fact]
    public void TheCeilingIsTheLargestPassLimitTheHistoryBudgetAdmits()
    {
        // Recomputed from the three measured constants, so a changed constant moves the
        // ceiling with it and nothing here carries the number itself.
        var expected = (InputWaitHistoryBudget.HistoryLimit
                        - InputWaitHistoryBudget.PrefixEvents
                        - InputWaitHistoryBudget.FaultEventsPerRun)
                       / InputWaitHistoryBudget.WorstCaseEventsPerPass;

        Assert.Equal(InputWaitHistoryBudget.MaxPasses, expected);
        Assert.True(InputWaitHistoryBudget.KeepsTheBound(InputWaitHistoryBudget.MaxPasses));
        Assert.False(InputWaitHistoryBudget.KeepsTheBound(InputWaitHistoryBudget.MaxPasses + 1));
    }

    [Fact]
    public void TheCeilingLeavesRoomForTheEventsOneWorkflowTaskTimeoutRecords()
    {
        // The same two statements the ceiling case made before the allowance was carried by the
        // product, now read from the product: the derived maximum is recomputed from the three
        // constants and the events of a run at it, with one timeout recorded, stay inside the
        // history. Nothing here carries the figure.
        var recomputed = (InputWaitHistoryBudget.HistoryLimit
                          - InputWaitHistoryBudget.PrefixEvents
                          - InputWaitHistoryBudget.FaultEventsPerRun)
                         / InputWaitHistoryBudget.WorstCaseEventsPerPass;
        var derived = InputWaitHistoryBudget.MaxPasses;
        var events = (derived * InputWaitHistoryBudget.WorstCaseEventsPerPass)
                     + InputWaitHistoryBudget.PrefixEvents
                     + InputWaitHistoryBudget.FaultEventsPerRun;

        Assert.Multiple(
            () => Assert.Equal(recomputed, derived),
            () => Assert.True(
                events <= InputWaitHistoryBudget.HistoryLimit,
                $"the derived maximum plus one workflow-task timeout writes {events} events, and the limit is {InputWaitHistoryBudget.HistoryLimit}."));
    }

    [Fact]
    public async Task TheFaultAllowanceIsTheCountTakenOnARecordedTimedOutWorkflowTask()
    {
        // The recording of a run in which the engine timed a workflow task out: the figure the
        // ceiling reserves is the pair of events one such timeout writes — the timeout itself and
        // the workflow task the engine schedules after it. The case fails if either the recording
        // or the constant moves, which is what makes the figure a checked measurement.
        var history = await JobHistoryFiles.ReadAsync("report-job-workflow-task-timeout.json");
        var timeouts = history.Events
            .Where(entry => entry.EventType == EventType.WorkflowTaskTimedOut)
            .ToList();
        Assert.NotEmpty(timeouts);

        var faultEvents = 0;
        foreach (var timeout in timeouts)
        {
            var following = history.Events
                .Where(entry => entry.EventId > timeout.EventId)
                .Where(entry => entry.EventType == EventType.WorkflowTaskScheduled)
                .MinBy(entry => entry.EventId);
            Assert.True(
                following is not null,
                $"the recorded timeout at event {timeout.EventId} is followed by no workflow task, so it measures nothing.");
            faultEvents += 2;
        }

        Assert.Equal(InputWaitHistoryBudget.FaultEventsPerRun, faultEvents);
    }

    [Fact]
    public void TheDefaultKeepsTheTighterFigureTheDecisionFixes()
    {
        var events = (InputWaitOptions.DefaultContinueAsNewAfter * InputWaitHistoryBudget.WorstCaseEventsPerPass)
            + InputWaitHistoryBudget.PrefixEvents;

        Assert.True(InputWaitHistoryBudget.TheDefaultKeepsTheCeiling);
        Assert.True(
            events <= InputWaitHistoryBudget.DefaultPassesCeiling,
            $"the default pass limit writes {events} events, and the figure it has to keep is {InputWaitHistoryBudget.DefaultPassesCeiling}.");
    }

    [Fact]
    public void EveryAcceptedPassLimitKeepsTheHistoryBound()
    {
        var options = new InputWaitOptions();

        Assert.True(
            (options.ContinueAsNewAfter * InputWaitHistoryBudget.WorstCaseEventsPerPass)
            + InputWaitHistoryBudget.PrefixEvents
            + InputWaitHistoryBudget.FaultEventsPerRun
            <= InputWaitHistoryBudget.HistoryLimit);
    }
}
