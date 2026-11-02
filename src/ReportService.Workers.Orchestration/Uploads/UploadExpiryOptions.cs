using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// How often the worker looks for uploads that have run out of time, and how many
/// of them one round expires. Both settings are required and bounded: an interval
/// below a second would keep the database busy for nothing, and a batch bounds the
/// work one round takes. A refused value stops the process at start, naming the
/// setting and the rule and never the value.
/// </summary>
public sealed class UploadExpiryOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Uploads:Expiry";

    /// <summary>The setting of the interval between two sweep rounds, relative to <see cref="SectionPath"/>.</summary>
    public const string SweepIntervalSetting = "SweepInterval";

    /// <summary>The setting of how many uploads one round expires, relative to <see cref="SectionPath"/>.</summary>
    public const string BatchSizeSetting = "BatchSize";

    /// <summary>The shortest accepted interval.</summary>
    public static readonly TimeSpan MinimumSweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>The longest accepted interval.</summary>
    public static readonly TimeSpan MaximumSweepInterval = TimeSpan.FromHours(1);

    /// <summary>The smallest accepted batch.</summary>
    public const int MinimumBatchSize = 1;

    /// <summary>The largest accepted batch.</summary>
    public const int MaximumBatchSize = 5000;

    /// <summary>How long the worker waits between two sweep rounds.</summary>
    public TimeSpan SweepInterval { get; set; }

    /// <summary>The most uploads one round expires.</summary>
    public int BatchSize { get; set; }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (SweepInterval < MinimumSweepInterval)
        {
            yield return new ConfigurationProblem(SweepIntervalSetting, "is required and must be at least one second");
        }
        else if (SweepInterval > MaximumSweepInterval)
        {
            yield return new ConfigurationProblem(SweepIntervalSetting, "must be one hour or less");
        }

        if (BatchSize < MinimumBatchSize || BatchSize > MaximumBatchSize)
        {
            yield return new ConfigurationProblem(BatchSizeSetting, "is required and must be between 1 and 5000");
        }
    }
}
