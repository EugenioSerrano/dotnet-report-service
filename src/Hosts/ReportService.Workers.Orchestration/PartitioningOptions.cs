using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration;

/// <summary>
/// The temporary single-block limit of PDF jobs: the configured record count an operator
/// may lower, capped by the block's hard maximum. This service version prints one block
/// per PDF report: a version that can print more than one block removes this setting, its
/// limit and its test in the same change. A missing or non-positive value stops the process
/// at start, naming the setting and never the value; a value above the hard maximum is
/// accepted, has no effect, and is reported once by the start-up line.
/// </summary>
public sealed class PartitioningOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Partitioning";

    /// <summary>The setting of the single-block record limit, relative to <see cref="SectionPath"/>.</summary>
    public const string SingleBlockRecordLimitSetting = "SingleBlockRecordLimit";

    /// <summary>The most records one PDF report may hold in this version.</summary>
    public int SingleBlockRecordLimit { get; set; }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (SingleBlockRecordLimit < 1)
        {
            yield return new ConfigurationProblem(
                SingleBlockRecordLimitSetting,
                "is required and must be greater than zero");
        }
    }
}
