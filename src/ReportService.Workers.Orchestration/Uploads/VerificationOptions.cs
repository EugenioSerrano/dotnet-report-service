using ReportService.ServiceDefaults;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// The size limits verification enforces: the number of stored bytes an upload
/// may have, and the number of content bytes its body may decompress to. Both
/// are required and must be greater than zero; a refused value stops the process
/// at start, naming the setting and the rule and never the value.
/// </summary>
public sealed class VerificationOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Uploads";

    /// <summary>The setting of the stored-size limit, relative to <see cref="SectionPath"/>.</summary>
    public const string MaxStoredBytesSetting = "MaxStoredBytes";

    /// <summary>The setting of the decompressed-size limit, relative to <see cref="SectionPath"/>.</summary>
    public const string MaxUncompressedBytesSetting = "MaxUncompressedBytes";

    /// <summary>The most stored bytes an upload may hold.</summary>
    public long MaxStoredBytes { get; set; }

    /// <summary>The most content bytes a body may decompress to.</summary>
    public long MaxUncompressedBytes { get; set; }

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (MaxStoredBytes <= 0)
        {
            yield return new ConfigurationProblem(MaxStoredBytesSetting, "is required and must be greater than zero");
        }

        if (MaxUncompressedBytes <= 0)
        {
            yield return new ConfigurationProblem(MaxUncompressedBytesSetting, "is required and must be greater than zero");
        }
    }
}
