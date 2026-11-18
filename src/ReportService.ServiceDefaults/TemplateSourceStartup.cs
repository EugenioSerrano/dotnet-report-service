using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Storage;
using ReportService.Storage.Filesystem;

namespace ReportService.ServiceDefaults;

/// <summary>
/// Opens the template source's own store while the process starts, after its
/// settings were validated and before it serves traffic or reports ready, and
/// proves what it will read: one entry of the class is listed and, when it is not
/// a record, the published prefix of that one template is listed; one publication
/// record is read verified when there is one, and one line states the provider,
/// the root, the class and the descriptor contract versions. The check's work does
/// not grow with the class's size: at most two one-entry listings and one verified
/// read, never a scan. It writes nothing, deletes nothing and sweeps nothing — a
/// read-only root is what it exists to accept. A root that is missing, is not a
/// directory, or holds a class that cannot be listed or read stops the start with a
/// <see cref="ConfigurationError"/> naming the setting and never a value or a path.
/// When the source's root is the process's writable root, one warning says the
/// read-only grant cannot be expressed on that deployment.
/// </summary>
public sealed partial class TemplateSourceStartup(
    IServiceProvider services,
    TemplateSourceContract contract,
    ILogger<TemplateSourceStartup> logger) : IHostedLifecycleService
{
    /// <summary>The longest the two bounded listings and the one record read may take.</summary>
    public static readonly TimeSpan TimeBound = TimeSpan.FromSeconds(5);

    /// <summary>What a class that cannot be listed is refused with.</summary>
    public const string CannotBeListed = "the template source class cannot be listed";

    /// <summary>What a class that cannot be read is refused with.</summary>
    public const string CannotBeRead = "the template source class cannot be read";

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        IObjectStore store;
        try
        {
            store = services.GetRequiredKeyedService<IObjectStore>(TemplateSourceStorage.StoreServiceKey);
        }
        catch (StorageConfigurationException exception)
        {
            throw new ConfigurationError(exception.Setting, exception.Reason);
        }

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeBound);

        (ObjectKey Key, long Size, string Sha256)? record;
        try
        {
            record = await FindOnePublicationAsync(store, bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConfigurationError(TemplateSourceStorage.RootSetting, CannotBeListed);
        }
        catch (StorageException)
        {
            throw new ConfigurationError(TemplateSourceStorage.RootSetting, CannotBeListed);
        }

        if (record is { } publication)
        {
            await ReadOnceAsync(store, publication.Key, publication.Size, publication.Sha256, bound.Token).ConfigureAwait(false);
        }

        var root = store is FilesystemObjectStore filesystem ? filesystem.Root : string.Empty;
        LogOpened(contract.Provider, root, contract.Layout.ClassPrefix, contract.VersionsText);
        if (WritableRoot() is { Length: > 0 } writable && SameDirectory(writable, root))
        {
            LogCoincident(root);
        }
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

    // One entry of the class listing, and at most one targeted listing of the first template's
    // published prefix: the check's work is the same for a class of one object and a class of
    // 150. When the class is empty, or the first template has not published yet, listability is
    // what start proves and the first take is the first content read.
    private async Task<(ObjectKey Key, long Size, string Sha256)?> FindOnePublicationAsync(IObjectStore store, CancellationToken cancellationToken)
    {
        var first = await FirstEntryAsync(store, contract.Layout.ClassPrefix, cancellationToken).ConfigureAwait(false);
        if (first is not { } entry)
        {
            return null;
        }

        if (contract.Layout.IsPublicationRecord(entry.Key, out var sha256))
        {
            return (entry.Key, entry.Size, sha256);
        }

        // segments[1] is the value KeySegmentEncoding produced; the encoding has no inverse, so
        // the segment is reused literally and never decoded into a template identifier.
        var segments = entry.Key.Segments;
        if (segments.Count < 2)
        {
            return null;
        }

        var published = await FirstEntryAsync(store, contract.Layout.PublishedPrefix(segments[1]), cancellationToken).ConfigureAwait(false);
        if (published is { } record && contract.Layout.IsPublicationRecord(record.Key, out var publishedSha256))
        {
            return (record.Key, record.Size, publishedSha256);
        }

        return null;
    }

    // Exactly one entry, and the enumerator is disposed: a class of 150 objects costs the same
    // as a class of one, and no part of the class is held in memory.
    private static async Task<(ObjectKey Key, long Size)?> FirstEntryAsync(IObjectStore store, string prefix, CancellationToken cancellationToken)
    {
        var enumerator = store.ListAsync(prefix, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false)
                ? (enumerator.Current.Key, enumerator.Current.Size)
                : null;
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    // One verified record, drained and dropped: the read needs neither the template's text nor the
    // version's, only that the class delivers the bytes its own name declares. The hash is the
    // record's own name without its descriptor suffix (segments[4] minus DescriptorSuffix), which
    // IsPublicationRecord has already read out.
    private static async Task ReadOnceAsync(IObjectStore store, ObjectKey key, long size, string sha256, CancellationToken cancellationToken)
    {
        try
        {
            var read = await store.OpenReadAsync(key, size, sha256, cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded)
            {
                throw new ConfigurationError(TemplateSourceStorage.RootSetting, CannotBeRead);
            }

            await using var verified = read.Value!;
            await verified.Content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException)
        {
            throw new ConfigurationError(TemplateSourceStorage.RootSetting, CannotBeRead);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConfigurationError(TemplateSourceStorage.RootSetting, CannotBeRead);
        }
    }

    // The process's writable root, when the process has one: it is what the source's root must not be.
    private string? WritableRoot()
    {
        try
        {
            return services.GetService<IObjectStore>() is FilesystemObjectStore store ? store.Root : null;
        }
        catch (StorageConfigurationException)
        {
            return null;
        }
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Information, Message = "Template source {Provider} at {Root}; class {Class}; descriptor contract versions {Versions}")]
    private partial void LogOpened(string provider, string root, string @class, string versions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The template source root and the writable storage root are the same directory ({Root}); this deployment cannot express the source's read-only grant")]
    private partial void LogCoincident(string root);
}
