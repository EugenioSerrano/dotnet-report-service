using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration.Retention;

/// <summary>
/// How long a published output stays downloadable: one duration, required and
/// greater than zero, bounded by the longest retention the service accepts. A
/// refused value stops the process at start, naming the setting and the rule
/// and never the value.
/// </summary>
public sealed class RetentionOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Retention";

    /// <summary>The setting of the output retention, relative to <see cref="SectionPath"/>.</summary>
    public const string OutputsSetting = "Outputs";

    /// <summary>The longest accepted retention.</summary>
    public static readonly TimeSpan MaximumOutputs = TimeSpan.FromDays(3650);

    /// <summary>
    /// How long after its publication an artifact may still be downloaded. The
    /// instant it expires at is recorded with the artifact when it is published.
    /// </summary>
    public TimeSpan Outputs { get; set; }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (Outputs <= TimeSpan.Zero)
        {
            yield return new ConfigurationProblem(OutputsSetting, "is required and must be greater than zero");
        }
        else if (Outputs > MaximumOutputs)
        {
            yield return new ConfigurationProblem(OutputsSetting, "must be 3650 days or less");
        }
    }
}
