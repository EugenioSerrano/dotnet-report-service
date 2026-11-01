using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>
/// How the process polls for durable requests and how long it may hold them: the
/// interval between polls, the batch it claims at once, the age at which a request
/// is loudly late, and the budget after which one poll stops working through its
/// batch. Every setting is required and bounded — the budget stays under the
/// database's own bound on an idle transaction, so a claim can never outlive the
/// act that holds it. A refused value stops the process at start, naming the
/// setting and never the value.
/// </summary>
public sealed class DispatcherOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Dispatcher";

    /// <summary>The setting of the interval between polls.</summary>
    public const string PollIntervalSetting = "PollInterval";

    /// <summary>The setting of the batch one poll claims.</summary>
    public const string BatchSizeSetting = "BatchSize";

    /// <summary>The setting of the age at which a pending request is reported.</summary>
    public const string AlertAfterSetting = "AlertAfter";

    /// <summary>The setting of the budget one poll works within.</summary>
    public const string ClaimBudgetSetting = "ClaimBudget";

    /// <summary>The shortest accepted poll interval.</summary>
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest accepted poll interval.</summary>
    public static readonly TimeSpan MaximumPollInterval = TimeSpan.FromMinutes(1);

    /// <summary>The smallest accepted batch.</summary>
    public const int MinimumBatchSize = 1;

    /// <summary>The largest accepted batch.</summary>
    public const int MaximumBatchSize = 500;

    /// <summary>The shortest accepted budget of one poll.</summary>
    public static readonly TimeSpan MinimumClaimBudget = TimeSpan.FromSeconds(5);

    /// <summary>The longest accepted budget of one poll.</summary>
    public static readonly TimeSpan MaximumClaimBudget = TimeSpan.FromSeconds(50);

    /// <summary>How long the process waits between two polls.</summary>
    public TimeSpan PollInterval { get; set; }

    /// <summary>The most requests one poll claims.</summary>
    public int BatchSize { get; set; }

    /// <summary>How long a request may stay pending before it is reported.</summary>
    public TimeSpan AlertAfter { get; set; }

    /// <summary>How long one poll works through its batch before leaving the rest.</summary>
    public TimeSpan ClaimBudget { get; set; }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (PollInterval < MinimumPollInterval || PollInterval > MaximumPollInterval)
        {
            yield return new ConfigurationProblem(PollIntervalSetting, "is required and must be between 100 ms and 1 minute");
        }

        if (BatchSize < MinimumBatchSize || BatchSize > MaximumBatchSize)
        {
            yield return new ConfigurationProblem(BatchSizeSetting, "is required and must be between 1 and 500");
        }

        if (AlertAfter <= TimeSpan.Zero)
        {
            yield return new ConfigurationProblem(AlertAfterSetting, "is required and must be greater than zero");
        }

        if (ClaimBudget < MinimumClaimBudget || ClaimBudget > MaximumClaimBudget)
        {
            yield return new ConfigurationProblem(ClaimBudgetSetting, "is required and must be between 5 and 50 seconds");
        }
    }
}
