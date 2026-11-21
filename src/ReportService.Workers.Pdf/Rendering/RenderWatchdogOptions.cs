using ReportService.ServiceDefaults;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// The render watchdog's two values: how long a render may pass its own deadline before
/// the watchdog acts, and how often the watchdog looks. The grace is a fixed 30 s over the
/// attempt's own deadline, looked at every 5 s. The values are built by the factory and
/// validated at start; they are never logged with a value a deployment configured, because
/// there is nothing to configure.
/// </summary>
/// <param name="GraceSeconds">The grace after an attempt's own deadline, in seconds.</param>
/// <param name="TickSeconds">How often the watchdog looks, in seconds.</param>
public sealed record RenderWatchdogOptions(int GraceSeconds = 30, int TickSeconds = 5)
{
    /// <summary>The setting name the validation reports, should a value ever be changed.</summary>
    public const string GraceSetting = "GraceSeconds";

    /// <summary>The setting name the validation reports, should a value ever be changed.</summary>
    public const string TickSetting = "TickSeconds";

    /// <summary>The grace after an attempt's own deadline.</summary>
    public TimeSpan Grace => TimeSpan.FromSeconds(GraceSeconds);

    /// <summary>How often the watchdog looks.</summary>
    public TimeSpan Tick => TimeSpan.FromSeconds(TickSeconds);

    /// <summary>Builds the service's fixed values.</summary>
    public static RenderWatchdogOptions Create() => new();

    /// <summary>The problems of a value set, each naming its setting and never the value.</summary>
    public IReadOnlyList<ConfigurationProblem> Validate()
    {
        var problems = new List<ConfigurationProblem>();
        if (GraceSeconds <= 0)
        {
            problems.Add(new(GraceSetting, "must be greater than zero"));
        }

        if (TickSeconds <= 0)
        {
            problems.Add(new(TickSetting, "must be greater than zero"));
        }
        else if (TickSeconds >= GraceSeconds)
        {
            problems.Add(new(TickSetting, "must be shorter than the grace"));
        }

        return problems;
    }
}
