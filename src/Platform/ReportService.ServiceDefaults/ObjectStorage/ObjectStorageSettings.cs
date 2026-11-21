using ReportService.Storage.Filesystem;

namespace ReportService.ServiceDefaults;

/// <summary>
/// The object storage settings of a process, in the <c>Storage</c> section:
/// <c>Storage:Provider</c> selects the provider and is required — there is no
/// default provider, so a process without it does not start; the development
/// settings select <c>filesystem</c>. <c>Storage:Filesystem</c> configures the
/// Filesystem provider and <c>Storage:Diagnostics:Enabled</c> mounts the
/// development-only diagnostics (off unless set).
/// </summary>
public sealed class ObjectStorageSettings : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Storage";

    /// <summary>The providers a process can select.</summary>
    public static readonly IReadOnlyList<string> KnownProviders = [FilesystemObjectStore.ProviderName];

    /// <summary>The selected provider; required.</summary>
    public string? Provider { get; set; }

    /// <summary>The Filesystem provider's settings.</summary>
    public FilesystemStorageOptions Filesystem { get; set; } = new();

    /// <summary>The development-only diagnostics.</summary>
    public StorageDiagnosticsSettings Diagnostics { get; set; } = new();

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            yield return new ConfigurationProblem(nameof(Provider), "is required; no provider is selected by default");
            yield break;
        }

        if (!KnownProviders.Contains(Provider, StringComparer.Ordinal))
        {
            yield return new ConfigurationProblem(nameof(Provider), $"is not a known provider (known: {string.Join(", ", KnownProviders)})");
            yield break;
        }

        foreach (var problem in Filesystem.Validate())
        {
            yield return new ConfigurationProblem($"{nameof(Filesystem)}:{problem.Setting}", problem.Reason);
        }
    }
}

/// <summary>The development-only storage diagnostics: off unless <see cref="Enabled"/> is set.</summary>
public sealed class StorageDiagnosticsSettings
{
    /// <summary>Whether the storage diagnostics endpoints are mounted.</summary>
    public bool Enabled { get; set; }
}
