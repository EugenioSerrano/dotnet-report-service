using ReportService.Workers.Orchestration.Jobs;

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
        var expected = (InputWaitHistoryBudget.HistoryLimit - InputWaitHistoryBudget.PrefixEvents - InputWaitHistoryBudget.FaultEventsPerRun)
            / InputWaitHistoryBudget.WorstCaseEventsPerPass;
        var derived = InputWaitHistoryBudget.MaxPasses;

        Assert.Equal(expected, derived);
        Assert.True(InputWaitHistoryBudget.KeepsTheBound(derived));
        Assert.False(InputWaitHistoryBudget.KeepsTheBound(derived + 1));
    }

    [Fact]
    public void TheCeilingReservesTheEventsAWorkflowTaskTimeoutRecords()
    {
        // Recomputed from the three constants the budget carries, so a re-measurement of any
        // of them moves the ceiling with it and nothing here carries the number itself.
        var derived = InputWaitHistoryBudget.MaxPasses;
        var recomputed = (InputWaitHistoryBudget.HistoryLimit - InputWaitHistoryBudget.PrefixEvents - InputWaitHistoryBudget.FaultEventsPerRun)
            / InputWaitHistoryBudget.WorstCaseEventsPerPass;
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
