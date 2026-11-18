using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Storage.Filesystem;

namespace ReportService.ServiceDefaults;

/// <summary>
/// The template source's own object store: a second instance of the deployment's
/// provider, on its own root, opened read-only and registered under a key of its
/// own, with the start check that proves its class can be listed and read and the
/// readiness check that keeps proving the class answers. It takes the class's
/// layout and the descriptor contract versions from its caller, so these
/// conventions name no template type and no package type.
/// </summary>
public static class TemplateSourceStorage
{
    /// <summary>The key the source's instance is registered under; nothing else resolves it.</summary>
    public const string StoreServiceKey = "template-source-store";

    /// <summary>The setting a refusal of the source's root names.</summary>
    public const string RootSetting =
        TemplateSourceStorageSettings.SectionPath + ":" + nameof(TemplateSourceStorageSettings.Filesystem) + ":" + nameof(FilesystemStorageOptions.Root);

    /// <summary>
    /// Registers the source's instance — an instance of <c>Storage:Provider</c>,
    /// never a provider of its own — binds and validates its section, and adds the
    /// start check and the <c>template-source</c> readiness check. The instance is
    /// registered under <see cref="StoreServiceKey"/> only: it is neither the
    /// process's <see cref="IObjectStore"/> nor a second registration of the
    /// concrete store, so no read falls back to the writable one.
    /// </summary>
    /// <param name="builder">The process being built.</param>
    /// <param name="layout">The layout of the class the source reads.</param>
    /// <param name="descriptorVersions">The publication-descriptor contract versions this build reads.</param>
    public static IHostApplicationBuilder AddTemplateSourceStorage(
        this IHostApplicationBuilder builder,
        TemplateSourceClassLayout layout,
        IReadOnlyList<string> descriptorVersions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(descriptorVersions);

        builder.AddValidatedConfiguration<TemplateSourceStorageSettings>(TemplateSourceStorageSettings.SectionPath);
        builder.Services.TryAddSingleton(TimeProvider.System);

        var provider = builder.Configuration[$"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Provider)}"] ?? string.Empty;
        if (string.Equals(provider, FilesystemObjectStore.ProviderName, StringComparison.Ordinal))
        {
            builder.Services.AddReadOnlyFilesystemObjectStore(
                StoreServiceKey,
                services => services.GetRequiredService<IOptions<TemplateSourceStorageSettings>>().Value.Filesystem,
                TemplateSourceStorageSettings.SectionPath + ":" + nameof(TemplateSourceStorageSettings.Filesystem),
                builder.Environment.ContentRootPath);
        }

        var contract = new TemplateSourceContract(provider, layout, descriptorVersions);
        builder.Services.AddSingleton(contract);
        builder.Services.AddHostedService<TemplateSourceStartup>();
        builder.Services.AddSingleton(services => new TemplateSourceHealthCheck(
            services.GetRequiredKeyedService<IObjectStore>(StoreServiceKey),
            contract,
            services.GetRequiredService<ILogger<TemplateSourceHealthCheck>>()));
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            TemplateSourceHealthCheck.Name,
            services => services.GetRequiredService<TemplateSourceHealthCheck>(),
            failureStatus: HealthStatus.Unhealthy,
            tags: null));
        return builder;
    }
}

/// <summary>The shape of the keys the source class holds, as the deployment's product writes them.</summary>
/// <param name="ClassPrefix">The first segment of every key of the class.</param>
/// <param name="PublishedSegment">The segment that holds a version's publication record.</param>
/// <param name="DescriptorSuffix">The constant suffix of a publication record's object name.</param>
/// <param name="DescriptorHashLength">The length of the SHA-256 text a record's name carries.</param>
public sealed record TemplateSourceClassLayout(string ClassPrefix, string PublishedSegment, string DescriptorSuffix, int DescriptorHashLength)
{
    /// <summary>The key of a publication record, read from a listing entry.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="sha256">The hash the record's name carries; empty when the entry is not a record.</param>
    public bool IsPublicationRecord(ObjectKey key, out string sha256)
    {
        sha256 = string.Empty;
        var segments = key.Segments;
        if (segments.Count != 5
            || !string.Equals(segments[0], ClassPrefix, StringComparison.Ordinal)
            || segments[1].Length == 0
            || !string.Equals(segments[2], PublishedSegment, StringComparison.Ordinal)
            || segments[3].Length == 0)
        {
            return false;
        }

        var name = segments[4];
        if (name.Length != DescriptorHashLength + DescriptorSuffix.Length
            || !name.EndsWith(DescriptorSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = name[..DescriptorHashLength];
        if (!candidate.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            return false;
        }

        sha256 = candidate;
        return true;
    }

    /// <summary>The published prefix of one template, as a listing takes it; the segment is reused literally, never decoded.</summary>
    /// <param name="templateSegment">The template's encoded segment, as the class listing carried it.</param>
    public string PublishedPrefix(string templateSegment) =>
        $"{ClassPrefix}/{templateSegment}/{PublishedSegment}";
}

/// <summary>What a process states about the class it reads and the contracts it accepts.</summary>
/// <param name="Provider">The deployment's provider this instance is an instance of.</param>
/// <param name="Layout">The layout of the class the source reads.</param>
/// <param name="DescriptorVersions">The publication-descriptor contract versions this build reads.</param>
public sealed record TemplateSourceContract(string Provider, TemplateSourceClassLayout Layout, IReadOnlyList<string> DescriptorVersions)
{
    /// <summary>The versions as one value a log line or a health body carries.</summary>
    public string VersionsText => string.Join(", ", DescriptorVersions);
}
