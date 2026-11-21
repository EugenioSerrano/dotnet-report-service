using ReportService.ServiceDefaults;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration;

/// <summary>
/// The settings of the worker process itself: how many verification attempts it
/// runs at the same time on the data queue. The setting is required and bounded;
/// a refused value stops the process at start, naming the setting and never the
/// value. The bound is what keeps one worker from taking more of the process's
/// memory than the deployment gave it.
/// </summary>
public sealed class WorkerOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Workers";

    /// <summary>The setting of the concurrent data slots, relative to <see cref="SectionPath"/>.</summary>
    public const string DataActivitySlotsSetting = "DataActivitySlots";

    /// <summary>The most concurrent attempts one process runs.</summary>
    public const int MaximumDataActivitySlots = 64;

    /// <summary>How many verification attempts this process runs at the same time.</summary>
    public int DataActivitySlots { get; set; }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (DataActivitySlots < 1 || DataActivitySlots > MaximumDataActivitySlots)
        {
            yield return new ConfigurationProblem(DataActivitySlotsSetting, "is required and must be between 1 and 64");
        }
    }
}
