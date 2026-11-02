using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// How long one verification attempt may run before the engine gives up on it.
/// The setting is required, must be longer than zero and is capped at one day: a
/// verification that outlasts its own bound is a defect, not a policy. A refused
/// value stops the process at start, naming the setting and the rule and never the
/// value.
/// </summary>
public sealed class UploadVerificationOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Uploads:Verification";

    /// <summary>The setting of the attempt's time bound, relative to <see cref="SectionPath"/>.</summary>
    public const string StartToCloseSetting = "StartToClose";

    /// <summary>The longest accepted bound of one attempt.</summary>
    public static readonly TimeSpan MaximumStartToClose = TimeSpan.FromDays(1);

    /// <summary>How long one verification attempt may run.</summary>
    public TimeSpan StartToClose { get; set; }

    /// <summary>
    /// The problem of a verification deadline that is shorter than one attempt, or
    /// <see langword="null"/> when the two fit together. The deadline a process uses
    /// lives in another settings record, so the rule between them is applied here,
    /// where the bound it measures against is known; a problem names both settings and
    /// never a value.
    /// </summary>
    /// <param name="deadline">The configured verification deadline.</param>
    public ConfigurationProblem? DeadlineProblem(TimeSpan deadline)
    {
        if (deadline <= TimeSpan.Zero || StartToClose <= TimeSpan.Zero || deadline >= StartToClose)
        {
            return null;
        }

        return new ConfigurationProblem(
            $"{VerificationOptions.SectionPath}:{VerificationOptions.VerificationDeadlineSetting}",
            $"must be at least as long as {SectionPath}:{StartToCloseSetting}");
    }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (StartToClose <= TimeSpan.Zero)
        {
            yield return new ConfigurationProblem(StartToCloseSetting, "is required and must be greater than zero");
        }
        else if (StartToClose > MaximumStartToClose)
        {
            yield return new ConfigurationProblem(StartToCloseSetting, "must be one day or less");
        }
    }
}
