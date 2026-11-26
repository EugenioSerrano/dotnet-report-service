using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The bounded-concurrency window of a multi-block PDF output: the number of block
/// render activities the child may keep outstanding. The default is two; a
/// configured value is read at process start and validated, and a value below one
/// is refused rather than silently replaced. The value is published to the
/// workflow type once, the same pattern the input wait uses, because the delivered
/// host creates workflows without a service provider.
/// </summary>
public static class PdfOutputConcurrencyOptions
{
    /// <summary>The configuration path of the window setting.</summary>
    public const string SettingPath = "Partitioning:PdfOutputWindow";

    /// <summary>The window when the deployment configures none: two renders at a time.</summary>
    public const int DefaultWindow = 2;

    /// <summary>The largest window one child may run: a bound that keeps the fan-out from exhausting the PDF workers.</summary>
    public const int MaximumWindow = 64;

    /// <summary>Resolves the effective window: the default when absent, otherwise the configured value, bounded.</summary>
    /// <param name="configured">The configured value, or <see langword="null"/> when none is configured.</param>
    public static int Resolve(int? configured)
    {
        if (configured is not { } value)
        {
            return DefaultWindow;
        }

        if (value < 1)
        {
            throw new ConfigurationError(
                [new ConfigurationProblem("PdfOutputWindow", "must be at least 1")]);
        }

        return Math.Min(value, MaximumWindow);
    }

    /// <summary>The validation problems of a configured window, empty when it is usable.</summary>
    /// <param name="configured">The configured value, or <see langword="null"/> when none is configured.</param>
    public static IReadOnlyList<ConfigurationProblem> Validate(int? configured) =>
        configured is { } value && value < 1
            ? [new ConfigurationProblem("PdfOutputWindow", "must be at least 1")]
            : [];
}
