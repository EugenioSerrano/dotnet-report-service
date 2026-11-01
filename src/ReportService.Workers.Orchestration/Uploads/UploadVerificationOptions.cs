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
