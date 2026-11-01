using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Storage.Filesystem;

namespace ReportService.ServiceDefaults;

/// <summary>
/// Opens the object store while the process starts, after its settings were
/// validated and before it serves traffic, and logs one line with the
/// provider, the root, the declared capabilities and the stale temporary
/// files removed. The root is a configuration value, logged here once and
/// never per object. A store that cannot open stops the start with a
/// <see cref="ConfigurationError"/> naming the setting.
/// </summary>
public sealed partial class ObjectStorageStartup(
    IServiceProvider services,
    IOptions<ObjectStorageSettings> settings,
    ILogger<ObjectStorageStartup> logger) : IHostedLifecycleService
{
    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        IObjectStore store;
        try
        {
            store = services.GetRequiredService<IObjectStore>();
        }
        catch (StorageConfigurationException exception)
        {
            throw new ConfigurationError(exception.Setting, exception.Reason);
        }

        if (store is FilesystemObjectStore filesystem)
        {
            LogOpened(settings.Value.Provider!, filesystem.Root, store.Capabilities.ToString(), filesystem.SweptTemporaryFiles);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Object storage provider {Provider} at {Root}; {Capabilities}; {Swept} stale temporary files removed")]
    private partial void LogOpened(string provider, string root, string capabilities, int swept);
}
