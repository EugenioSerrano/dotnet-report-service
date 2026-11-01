using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ReportService.Storage.Filesystem;

/// <summary>Registers the Filesystem provider as the process's <see cref="IObjectStore"/>.</summary>
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
}
