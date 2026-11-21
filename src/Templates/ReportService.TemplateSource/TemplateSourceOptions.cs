using ReportService.ServiceDefaults;

namespace ReportService.TemplateSource;

/// <summary>
/// The settings of the template source, in the <c>TemplateSource</c> section: how
/// long one by-name resolution may take, and the caps one version's package must
/// stay inside. Every member named here has a reader — the resolution's deadline
/// bounds the step that resolves a name, and the three caps bound the publication
/// record's own checks — and a problem names its setting and never its value.
/// </summary>
public sealed class TemplateSourceOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "TemplateSource";

    /// <summary>The smallest resolution deadline, in seconds.</summary>
    public const int MinResolveDeadlineSeconds = 5;

    /// <summary>The largest resolution deadline, in seconds.</summary>
    public const int MaxResolveDeadlineSeconds = 300;

    /// <summary>The fewest files a package may hold.</summary>
    public const int MinFiles = 1;

    /// <summary>The most files a package may hold.</summary>
    public const int MaxFilesLimit = 256;

    /// <summary>The smallest file a package may hold, in bytes.</summary>
    public const int MinFileBytes = 1024;

    /// <summary>The largest file a package may hold, in bytes.</summary>
    public const int MaxFileBytesLimit = 8 * 1024 * 1024;

    /// <summary>The largest package a take accepts, in bytes.</summary>
    public const int MaxPackageBytesLimit = 32 * 1024 * 1024;

    /// <summary>
    /// How long one by-name resolution — the listing and every version it takes —
    /// may take, measured from the step that resolves it. It fits inside the step's
    /// own bound, and it can only shorten a take, never lengthen one.
    /// </summary>
    public TimeSpan ResolveDeadline { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>The most files a package may hold.</summary>
    public int MaxFiles { get; set; } = MaxFilesLimit;

    /// <summary>The largest file a package may hold, in bytes.</summary>
    public int MaxFileBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>The largest package a package may add up to, in bytes.</summary>
    public int MaxPackageBytes { get; set; } = 16 * 1024 * 1024;

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (ResolveDeadline < TimeSpan.FromSeconds(MinResolveDeadlineSeconds) || ResolveDeadline > TimeSpan.FromSeconds(MaxResolveDeadlineSeconds))
        {
            yield return new ConfigurationProblem(nameof(ResolveDeadline), $"is not between {MinResolveDeadlineSeconds} and {MaxResolveDeadlineSeconds} seconds");
        }

        if (MaxFiles < MinFiles || MaxFiles > MaxFilesLimit)
        {
            yield return new ConfigurationProblem(nameof(MaxFiles), $"is not between {MinFiles} and {MaxFilesLimit}");
        }

        if (MaxFileBytes < MinFileBytes || MaxFileBytes > MaxFileBytesLimit)
        {
            yield return new ConfigurationProblem(nameof(MaxFileBytes), $"is not between {MinFileBytes} and {MaxFileBytesLimit} bytes");
        }

        if (MaxPackageBytes <= 0 || MaxPackageBytes > MaxPackageBytesLimit)
        {
            yield return new ConfigurationProblem(nameof(MaxPackageBytes), $"is not between 1 and {MaxPackageBytesLimit} bytes");
        }
    }
}
