using ReportService.Uploads;

using ReportService.ServiceDefaults;

namespace ReportService.Api.Uploads;

/// <summary>
/// How long an upload session accepts data. The setting is required, must be
/// longer than zero and no longer than seven days: a lifetime kept in days and
/// entered in hours would otherwise hold staging bytes far longer than intended.
/// A refused value stops the process at start, naming the setting and the rule
/// and never the value.
/// </summary>
public sealed class UploadOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Uploads";

    /// <summary>The setting of the session lifetime, relative to <see cref="SectionPath"/>.</summary>
    public const string SessionLifetimeSetting = "SessionLifetime";

    /// <summary>The longest accepted lifetime.</summary>
    public static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromDays(7);

    /// <summary>How long a new session accepts data.</summary>
    public TimeSpan SessionLifetime { get; set; }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (SessionLifetime <= TimeSpan.Zero)
        {
            yield return new ConfigurationProblem(SessionLifetimeSetting, "is required and must be greater than zero");
        }
        else if (SessionLifetime > MaximumSessionLifetime)
        {
            yield return new ConfigurationProblem(SessionLifetimeSetting, "must be seven days or less");
        }
    }
}
