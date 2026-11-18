using ReportService.Storage.Filesystem;

namespace ReportService.ServiceDefaults;

/// <summary>
/// The settings of the template source's own object store, in the
/// <c>TemplateSource:Store</c> section: the provider's sub-section, with the
/// root the source class lives under and the read bound, and the creation of a
/// missing root refused. The provider itself is not a member — it is the
/// deployment's <c>Storage:Provider</c>, because the source reads through a
/// second instance of that provider, never through a second provider. Nothing
/// here is a secret.
/// </summary>
public sealed class TemplateSourceStorageSettings : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "TemplateSource:Store";

    /// <summary>The Filesystem provider's sub-section: the root, the read buffer and the creation flag.</summary>
    public FilesystemStorageOptions Filesystem { get; set; } = new();

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        foreach (var problem in Filesystem.Validate())
        {
            yield return new ConfigurationProblem($"{nameof(Filesystem)}:{problem.Setting}", problem.Reason);
        }

        if (Filesystem.CreateIfMissing)
        {
            yield return new ConfigurationProblem(
                $"{nameof(Filesystem)}:{nameof(FilesystemStorageOptions.CreateIfMissing)}",
                "must be false: the source reads a root a publisher creates, never one it creates itself");
        }
    }
}
