using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ReportService.Storage.Filesystem;

/// <summary>Registers the Filesystem provider as the process's <see cref="IObjectStore"/> and, beside it, a read-only instance under a key of its own.</summary>
public static class FilesystemStorageRegistration
{
    /// <summary>
    /// Registers one <see cref="FilesystemObjectStore"/>, opened from
    /// <paramref name="options"/> on first use, as the process's
    /// <see cref="IObjectStore"/>. A relative root is resolved against
    /// <paramref name="contentRootPath"/>.
    /// </summary>
    public static IServiceCollection AddFilesystemObjectStore(
        this IServiceCollection services,
        Func<IServiceProvider, FilesystemStorageOptions> options,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        services.AddSingleton(provider => FilesystemObjectStore.Open(
            options(provider),
            contentRootPath,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILogger<FilesystemObjectStore>>()));
        services.AddSingleton<IObjectStore>(provider => provider.GetRequiredService<FilesystemObjectStore>());
        return services;
    }

    /// <summary>
    /// Registers a second <see cref="FilesystemObjectStore"/>, opened read-only
    /// from <paramref name="options"/> on first use, under <paramref name="serviceKey"/>
    /// only: it is neither the process's <see cref="IObjectStore"/> nor a second
    /// registration of the concrete type, so nothing can reach it by accident and
    /// no reader falls back to the writable store. A relative root is resolved
    /// against <paramref name="contentRootPath"/>, and a refusal names
    /// <paramref name="sectionPath"/>'s setting.
    /// </summary>
    public static IServiceCollection AddReadOnlyFilesystemObjectStore(
        this IServiceCollection services,
        string serviceKey,
        Func<IServiceProvider, FilesystemStorageOptions> options,
        string sectionPath,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        services.AddKeyedSingleton<IObjectStore>(serviceKey, (provider, _) => FilesystemObjectStore.OpenReadOnly(
            options(provider),
            sectionPath,
            contentRootPath,
            provider.GetRequiredService<ILogger<FilesystemObjectStore>>()));
        return services;
    }
}
