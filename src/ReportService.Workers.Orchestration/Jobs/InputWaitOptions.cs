using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// How long a job waits between two checks of the upload it reports on, and how many
/// checks one run of the workflow may take before it continues as a new run. Both
/// settings are bounded and a refused value stops the process at start, naming the
/// setting and the rule and never the value: the interval is a correctness bound and
/// not a preference, and the number of checks is bounded by the history a run may
/// carry. The ceiling is derived net of the events one workflow-task timeout and the
/// task that follows it write, so a run at that ceiling that records one of them still
/// stays inside the bound, and a run that recorded six or more would not.
/// </summary>
public sealed class InputWaitOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Jobs:InputWait";

    /// <summary>The setting of the interval between two checks, relative to <see cref="SectionPath"/>.</summary>
    public const string BackstopSetting = "Backstop";

    /// <summary>The setting of the checks one run may take, relative to <see cref="SectionPath"/>.</summary>
    public const string ContinueAsNewAfterSetting = "ContinueAsNewAfter";

    /// <summary>How long a job waits between two checks when nothing else is configured.</summary>
    public static readonly TimeSpan DefaultBackstop = TimeSpan.FromSeconds(30);

    /// <summary>The shortest accepted interval between two checks.</summary>
    public static readonly TimeSpan MinimumBackstop = TimeSpan.FromSeconds(1);

    /// <summary>The longest accepted interval between two checks.</summary>
    public static readonly TimeSpan MaximumBackstop = TimeSpan.FromMinutes(5);

    /// <summary>The checks one run takes before it continues as new when nothing else is configured.</summary>
    public const int DefaultContinueAsNewAfter = 100;

    /// <summary>How long a job waits between two checks of its upload.</summary>
    public TimeSpan Backstop { get; set; } = DefaultBackstop;

    /// <summary>The checks one run of the workflow may take before it continues as new.</summary>
    public int ContinueAsNewAfter { get; set; } = DefaultContinueAsNewAfter;

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        // The three constants the ceiling is derived from are checked against the figure the
        // decision fixes the first time this type is read: a measurement that pushed the
        // default over it stops the process rather than moving the figure (see
        // <see cref="InputWaitHistoryBudget"/>).
        if (Backstop < MinimumBackstop)
        {
            yield return new ConfigurationProblem(BackstopSetting, "is required and must be at least one second");
        }
        else if (Backstop > MaximumBackstop)
        {
            yield return new ConfigurationProblem(BackstopSetting, "must be five minutes or less");
        }

        if (ContinueAsNewAfter < 1 || !InputWaitHistoryBudget.KeepsTheBound(ContinueAsNewAfter))
        {
            yield return new ConfigurationProblem(
                ContinueAsNewAfterSetting,
                "is required and must be a number of checks the history of one run can carry");
        }
    }
}

/// <summary>
/// The arithmetic behind the pass limit, in one place: a run of the workflow may not
/// carry more events than the engine's history holds, so the number of checks it takes
/// is bounded by what is left of that history after the run's own fixed events and the
/// events one workflow-task timeout writes, which the derivation reserves. The ceiling
/// is <b>derived</b> from the three counted values rather than written down, so a later
/// measurement moves it with no code changed and no test carrying a number.
/// </summary>
public static class InputWaitHistoryBudget
{
    /// <summary>The events one run of a workflow may carry.</summary>
    public const int HistoryLimit = 10_000;

    /// <summary>The events one wait between two checks writes, counted against a real engine.</summary>
    public const int WorstCaseEventsPerPass = 11;

    /// <summary>The events a run writes whatever it does: its start, its two stage writes and its end.</summary>
    public const int PrefixEvents = 100;

    /// <summary>The events one workflow-task timeout and the workflow task that follows it write.</summary>
    public const int FaultEventsPerRun = 2;

    /// <summary>The largest number of checks one run may take and still fit the history.</summary>
    public const int MaxPasses = (HistoryLimit - PrefixEvents - FaultEventsPerRun) / WorstCaseEventsPerPass;

    /// <summary>The event figure the default number of checks has to keep.</summary>
    public const int DefaultPassesCeiling = 1_200;

    /// <summary>
    /// Refuses to be initialized when the three counted values no longer leave the default
    /// number of checks inside the figure the decision fixes, so a re-measurement that
    /// changed the arithmetic is a failure at start rather than a quiet departure.
    /// </summary>
    /// <exception cref="InvalidOperationException">The default no longer fits.</exception>
    static InputWaitHistoryBudget()
    {
        if (!TheDefaultKeepsTheCeiling)
        {
            throw new InvalidOperationException(
                "the default number of checks no longer fits the events a run may carry; the three counted values have to be re-measured.");
        }
    }

    /// <summary>Whether a run that takes <paramref name="passes"/> checks stays inside the history.</summary>
    /// <param name="passes">The number of checks the run takes.</param>
    public static bool KeepsTheBound(int passes) =>
        (passes * WorstCaseEventsPerPass) + PrefixEvents + FaultEventsPerRun <= HistoryLimit;

    /// <summary>Whether the default number of checks stays inside the figure the decision fixes.</summary>
    public static bool TheDefaultKeepsTheCeiling =>
        (InputWaitOptions.DefaultContinueAsNewAfter * WorstCaseEventsPerPass) + PrefixEvents <= DefaultPassesCeiling;
}
