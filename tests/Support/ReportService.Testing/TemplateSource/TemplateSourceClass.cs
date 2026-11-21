using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The template source class as a test fixture: it publishes a version the way
/// the owner's pipeline does - every file first, create-only, then the
/// publication record whose object name is its own SHA-256 - over the real
/// Filesystem provider on a root of the test's own. It is not a double: the
/// read is exercised against the same provider a deployment receives, so the
/// verified read and the paged listing are the provider's own.
/// <para>
/// The fixture also writes the refused shapes: a record whose files are absent,
/// a raw object outside the layout, a record written to a contract this build
/// does not read, and a corrupted object at a key the store already published.
/// </para>
/// </summary>
public sealed class TemplateSourceClass : IAsyncDisposable
{
    private readonly string? _root;
    private readonly FilesystemObjectStore? _owned;
    private readonly List<string> _writes = [];

    private TemplateSourceClass(string? root, IObjectStore store, FilesystemObjectStore? owned)
    {
        _root = root;
        Store = store;
        _owned = owned;
    }

    /// <summary>The store the fixture publishes through and the read is given.</summary>
    public IObjectStore Store { get; }

    /// <summary>The keys the fixture itself wrote, in the order it wrote them.</summary>
    public IReadOnlyList<string> Writes => _writes;

    /// <summary>The root the fixture owns, when it opened the store itself.</summary>
    public string? Root => _root;

    /// <summary>Opens a Filesystem store on <paramref name="root"/> and publishes through it.</summary>
    /// <param name="root">The temporary root of the class.</param>
    public static Task<TemplateSourceClass> CreateAsync(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);
        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root, CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        return Task.FromResult(new TemplateSourceClass(Path.GetFullPath(root), store, store));
    }

    /// <summary>Publishes through a store the caller already owns, without opening a second one.</summary>
    /// <param name="store">The store the fixture writes to.</param>
    public static TemplateSourceClass Over(IObjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new TemplateSourceClass(root: null, store, owned: null);
    }

    /// <summary>
    /// Publishes one version the way the owner's pipeline does: every file
    /// create-only, then the publication record under its own SHA-256.
    /// </summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version the record declares.</param>
    /// <param name="files">The package's files, by package-relative path.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public async Task PublishAsync(
        string templateId,
        string version,
        IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(files);

        var entries = new List<RecordFile>(files.Count);
        foreach (var file in files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            await WriteFileCoreAsync(templateId, version, file.Key, file.Value, CancellationToken.None).ConfigureAwait(false);
            entries.Add(new RecordFile(file.Key, file.Value.Length, Sha256(file.Value)));
        }

        await WriteRecordCoreAsync(templateId, version, RecordJson(templateId, version, entries), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a publication record naming files the class does not hold, or
    /// whose bytes are whatever an argument names - the incomplete and invalid
    /// shapes a read must refuse.
    /// </summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version the record declares.</param>
    /// <param name="files">The files the record names, with the size and SHA-256 it declares.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public Task PublishRecordOnlyAsync(
        string templateId,
        string version,
        IReadOnlyList<(string Path, long Size, string Sha256)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var entries = files.Select(file => new RecordFile(file.Path, file.Size, file.Sha256)).ToList();
        return WriteRecordCoreAsync(templateId, version, RecordJson(templateId, version, entries), CancellationToken.None);
    }

    /// <summary>Writes one package file under the version's files prefix, create-only.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="path">The package-relative path.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public Task WriteFileAsync(
        string templateId,
        string version,
        string path,
        byte[] bytes) =>
        WriteFileCoreAsync(templateId, version, path, bytes, CancellationToken.None);

    private async Task WriteFileCoreAsync(
        string templateId,
        string version,
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var key = TemplateSourceKeys.File(templateId, version, path);
        var write = await Store.WriteAsync(key, new MemoryStream(bytes, writable: false), cancellationToken)
            .ConfigureAwait(false);
        if (write.Outcome is not (StorageOutcome.Success or StorageOutcome.AlreadyExists))
        {
            throw new InvalidOperationException($"the fixture could not write the key: {write.Outcome} {write.Error?.Code}");
        }

        _writes.Add(key.ToString());
    }

    /// <summary>Writes one raw object under a key the test builds.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="bytes">The object's bytes.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public async Task WriteRawFileAsync(string key, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var write = await Store.WriteAsync(ObjectKey.Create(key), new MemoryStream(bytes, writable: false), CancellationToken.None)
            .ConfigureAwait(false);
        if (write.Outcome is not (StorageOutcome.Success or StorageOutcome.AlreadyExists))
        {
            throw new InvalidOperationException($"the fixture could not write the raw key: {write.Outcome} {write.Error?.Code}");
        }

        _writes.Add(key);
    }

    /// <summary>
    /// Writes a record with the bytes the caller gives, under the key its own
    /// SHA-256 names - the shape a publisher outside this build may write.
    /// </summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version the record is published under.</param>
    /// <param name="json">The record's bytes.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The key the record was written at.</returns>
    public Task<ObjectKey> WriteRecordAsync(string templateId, string version, byte[] json) =>
        WriteRecordCoreAsync(templateId, version, json, CancellationToken.None);

    private async Task<ObjectKey> WriteRecordCoreAsync(
        string templateId,
        string version,
        byte[] json,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        var key = TemplateSourceKeys.Descriptor(templateId, version, Sha256(json));
        var write = await Store.WriteAsync(key, new MemoryStream(json, writable: false), cancellationToken)
            .ConfigureAwait(false);
        if (write.Outcome is not (StorageOutcome.Success or StorageOutcome.AlreadyExists))
        {
            throw new InvalidOperationException($"the fixture could not write the record: {write.Outcome} {write.Error?.Code}");
        }

        _writes.Add(key.ToString());
        return key;
    }

    /// <summary>Removes every object of one version, as a publisher removing its folder.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public async Task RemoveAsync(string templateId, string version)
    {
        foreach (var prefix in new[] { TemplateSourceKeys.PublishedVersionPrefix(templateId, version), TemplateSourceKeys.FilesPrefix(templateId, version) })
        {
            var keys = new List<ObjectKey>();
            await foreach (var info in Store.ListAsync(prefix, CancellationToken.None).ConfigureAwait(false))
            {
                keys.Add(info.Key);
            }

            foreach (var key in keys)
            {
                await Store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Deletes one object of the class through the store, as a publisher's cleanup does.</summary>
    /// <param name="key">The key to delete.</param>
    public async Task DeleteAsync(ObjectKey key) =>
        await Store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Replaces an object's bytes behind the store's back, as a corrupted
    /// publisher would: the key still names the old hash. Only a fixture that
    /// owns its root can do this.
    /// </summary>
    /// <param name="key">The key to corrupt.</param>
    /// <param name="bytes">The bytes now held at it.</param>
    public void Corrupt(ObjectKey key, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (_root is null)
        {
            throw new InvalidOperationException("a fixture over a caller's store cannot write behind it");
        }

        var path = Path.Combine([_root, .. key.Segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Deletes one object from the fixture's own root.</summary>
    /// <param name="key">The key to delete.</param>
    public void Delete(ObjectKey key)
    {
        if (_root is null)
        {
            throw new InvalidOperationException("a fixture over a caller's store cannot write behind it");
        }

        var path = Path.Combine([_root, .. key.Segments]);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>A reader over the same store, with the deployment's caps and no logging.</summary>
    /// <param name="options">The settings; the defaults when omitted.</param>
    public StorageTemplateSource Reader(TemplateSourceOptions? options = null) =>
        new(
            Store,
            options ?? new TemplateSourceOptions(),
            new TemplateSourceMetrics(),
            NullLogger<StorageTemplateSource>.Instance);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The SHA-256 of the bytes, lower-case hexadecimal.</summary>
    /// <param name="bytes">The bytes.</param>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] RecordJson(string templateId, string version, IReadOnlyList<RecordFile> files)
    {
        var body = new
        {
            descriptorVersion = "1.0",
            templateId,
            version,
            files = files.Select(file => new { path = file.Path, size = file.Size, sha256 = file.Sha256 }).ToArray(),
            fileCount = files.Count,
            totalSize = files.Sum(file => file.Size),
        };
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private sealed record RecordFile(string Path, long Size, string Sha256);
}

