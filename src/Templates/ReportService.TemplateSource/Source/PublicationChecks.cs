using System.Globalization;
using ReportService.Storage;
using ReportService.TemplatePackages.Packages;

namespace ReportService.TemplateSource.Source;

/// <summary>
/// The publication record's own rules, in one fixed order so the same defect
/// always answers the same text: the contract version, the template, the version,
/// the file count, every path, repeats and file-and-folder collisions, every size,
/// every hash, the total, the declared counts and the root manifest. The caps are
/// the deployment's delivered source settings; the read adds none of its own. A
/// reason names a file, a field or a cap - never a storage key, a credential, a
/// provider message or a record's raw bytes.
/// </summary>
public static class PublicationChecks
{
    /// <summary>The one record contract version this build reads.</summary>
    public const string DescriptorVersion = "1.0";

    /// <summary>Every record contract version this build reads: the one above, stated at start and on the check.</summary>
    public static readonly IReadOnlyList<string> DescriptorVersions = [DescriptorVersion];

    /// <summary>Checks one parsed record against the requested version and the deployment's caps.</summary>
    /// <param name="descriptor">The parsed record.</param>
    /// <param name="templateId">The requested template's identifier.</param>
    /// <param name="version">The requested version.</param>
    /// <param name="options">The deployment's caps.</param>
    /// <returns>The one refusal the first failing rule produces, or <see langword="null"/> when the record passes.</returns>
    public static SourceReadRefusal? Check(
        PublicationDescriptor descriptor,
        string templateId,
        string version,
        TemplateSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        // 1. The contract version this build reads.
        if (!string.Equals(descriptor.DescriptorVersion, DescriptorVersion, StringComparison.Ordinal))
        {
            return new SourceReadRefusal(
                SourceReadRefusalKind.Unsupported,
                $"The publication record was written to contract version '{Printable(descriptor.DescriptorVersion)}', which this service does not read.");
        }

        // 2. The template the record declares.
        if (!string.Equals(descriptor.TemplateId, templateId, StringComparison.Ordinal))
        {
            return Invalid($"The publication record declares template '{Printable(descriptor.TemplateId)}' but template '{Printable(templateId)}' was requested.");
        }

        // 3. The version the record declares, exactly.
        if (!string.Equals(descriptor.Version, version, StringComparison.Ordinal))
        {
            return Invalid($"The publication found for version '{Printable(version)}' declares version '{Printable(descriptor.Version)}'.");
        }

        // 4. A non-empty file count inside the deployment's cap.
        if (descriptor.Files.Count == 0 || descriptor.Files.Count > options.MaxFiles)
        {
            return Invalid(
                $"The publication record names {descriptor.Files.Count.ToString(CultureInfo.InvariantCulture)} file(s); this service takes between {TemplateSourceOptions.MinFiles.ToString(CultureInfo.InvariantCulture)} and {options.MaxFiles.ToString(CultureInfo.InvariantCulture)}.");
        }

        // 5. Every path a package may hold.
        foreach (var file in descriptor.Files)
        {
            if (!PackagePath.IsValid(file.Path, out var pathReason))
            {
                return Invalid($"The publication record names the file '{Printable(file.Path)}', which is not a path a template package may hold: {pathReason}.");
            }
        }

        // 6. No path repeats, and no path is also a folder of another entry.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in descriptor.Files)
        {
            if (!paths.Add(file.Path))
            {
                return Invalid($"The publication record names the file '{Printable(file.Path)}' more than once.");
            }
        }

        foreach (var path in paths)
        {
            for (var index = path.IndexOf('/', StringComparison.Ordinal); index > 0; index = path.IndexOf('/', index + 1))
            {
                if (paths.Contains(path[..index]))
                {
                    return Invalid($"The publication record names '{Printable(path)}' both as a file and as a folder of another entry.");
                }
            }
        }

        // 7. Every size a file of a package may have.
        foreach (var file in descriptor.Files)
        {
            if (file.Size <= 0 || file.Size > options.MaxFileBytes)
            {
                return Invalid(
                    $"The file '{Printable(file.Path)}' has {file.Size.ToString(CultureInfo.InvariantCulture)} bytes; this service takes files between 1 and {options.MaxFileBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            }
        }

        // 8. Every hash a SHA-256 text.
        foreach (var file in descriptor.Files)
        {
            if (!Sha256Text.IsValid(file.Sha256))
            {
                return Invalid($"The file '{Printable(file.Path)}' does not declare a SHA-256 of 64 lower-case hexadecimal characters.");
            }
        }

        // 9. The total inside the deployment's cap.
        var total = descriptor.Files.Sum(file => file.Size);
        if (total > options.MaxPackageBytes)
        {
            return Invalid(
                $"The publication's files add up to {total.ToString(CultureInfo.InvariantCulture)} bytes; this service takes at most {options.MaxPackageBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }

        // 10. The declared counts and total agree with the entries.
        if (descriptor.FileCount != descriptor.Files.Count)
        {
            return Invalid(
                $"The publication record declares {descriptor.FileCount.ToString(CultureInfo.InvariantCulture)} file(s) and its entries hold {descriptor.Files.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (descriptor.TotalSize != total)
        {
            return Invalid(
                $"The publication record declares {descriptor.TotalSize.ToString(CultureInfo.InvariantCulture)} byte(s) and its entries add up to {total.ToString(CultureInfo.InvariantCulture)}.");
        }

        // 11. The root manifest.
        if (!paths.Contains(PackagePath.ManifestFileName))
        {
            return Invalid("The publication record does not name template.yaml at the root.");
        }

        return null;
    }

    /// <summary>The text a message may echo: printable ASCII of at most 128 characters, or a placeholder.</summary>
    /// <param name="value">The value a message would name.</param>
    public static string Printable(string? value)
    {
        if (value is null || value.Length is 0 or > 128 || value.Any(character => character < '\u0020' || character > '\u007E'))
        {
            return "(omitted)";
        }

        return value;
    }

    private static SourceReadRefusal Invalid(string reason) => new(SourceReadRefusalKind.Invalid, reason);
}
