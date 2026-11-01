using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Storage.Filesystem;

namespace ReportService.ServiceDefaults;

/// <summary>How a process gets its object store, its storage health check and its storage diagnostics.</summary>
public static class ObjectStorageHosting
{
    /// <summary>
    /// Registers the process's <see cref="IObjectStore"/>, selected by
    /// <c>Storage:Provider</c> and validated at start, and the
    /// <c>storage</c> health check, which writes, reads and deletes a probe
    /// object under <c>staging/probe/&lt;processName&gt;/</c>. The store is
    /// opened before the process serves traffic: a root that is missing, not
    /// a directory or not writable stops the start naming the setting.
    /// </summary>
    public static IHostApplicationBuilder AddObjectStorage(this IHostApplicationBuilder builder, string processName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var probeSegment = ObjectKey.FromSegments(processName).ToString();

        builder.AddValidatedConfiguration<ObjectStorageSettings>(ObjectStorageSettings.SectionPath);
        builder.Services.TryAddSingleton(TimeProvider.System);
        var provider = builder.Configuration[$"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Provider)}"];
        if (string.Equals(provider, FilesystemObjectStore.ProviderName, StringComparison.Ordinal))
        {
            builder.Services.AddFilesystemObjectStore(
                services => services.GetRequiredService<IOptions<ObjectStorageSettings>>().Value.Filesystem,
                builder.Environment.ContentRootPath);
        }

        builder.Services.AddHostedService<ObjectStorageStartup>();
        builder.Services.AddSingleton(services => new StorageHealthCheck(
            services.GetRequiredService<IObjectStore>(),
            services.GetRequiredService<IOptions<ObjectStorageSettings>>().Value.Provider!,
            probeSegment,
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StorageHealthCheck>>()));
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            StorageHealthCheck.Name,
            services => services.GetRequiredService<StorageHealthCheck>(),
            failureStatus: HealthStatus.Unhealthy,
            tags: null));
        return builder;
    }
}
