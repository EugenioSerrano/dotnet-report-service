using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The deployment's aging wait: how long a low job's render or merge work may stay pending
/// before the job is promoted to normal for its later dispatch. The product default is ten
/// minutes; the value is the deployment's, within a range that keeps the sampling meaningful.
/// </summary>
public sealed class PriorityAgingOptions : IValidatedSettings
{
    /// <summary>The section the setting is read from.</summary>
    public const string SectionPath = "Priority:Aging";

    /// <summary>The setting's name in the section.</summary>
    public const string WaitSetting = "Wait";

    /// <summary>The product default.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(10);

    /// <summary>The shortest wait a deployment may configure.</summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait a deployment may configure.</summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromDays(7);

    /// <summary>The configured wait.</summary>
    public TimeSpan Wait { get; set; } = DefaultWait;

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (Wait < MinimumWait || Wait > MaximumWait)
        {
            yield return new ConfigurationProblem(WaitSetting, "must be between five seconds and seven days");
        }
    }
}
