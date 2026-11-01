using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace ReportService.Storage.Filesystem;

/// <summary>
/// The Filesystem provider of <see cref="IObjectStore"/>: an object is a file
/// at <c>&lt;root&gt;/&lt;segment&gt;/…/&lt;segment&gt;</c>. Every call checks
/// that the resolved path lies under the canonical root — through links too —
/// and is no longer than <see cref="MaxPathLength"/>. A write streams to a
/// temporary file in the target directory, flushes it to disk and publishes
/// it with a create-only operation, so an existing object is never
/// overwritten and a partial object is never visible under its key. Failures
/// are outcomes whose messages carry no key and no path; each is logged once
/// with its operation and code only.
/// </summary>
public sealed partial class FilesystemObjectStore : IObjectStore
{
    /// <summary>The provider's name in configuration.</summary>
    public const string ProviderName = "filesystem";

    /// <summary>The longest resolved path accepted, leaving room for the temporary-file suffix.</summary>
    public const int MaxPathLength = 248;

    /// <summary>The number of objects a listing reads before handing them on.</summary>
    public const int ListPageSize = 256;

    /// <summary>How old a temporary file must be before the start-up sweep deletes it.</summary>
    public static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(24);

    private const string RootSetting = FilesystemStorageOptions.SectionPath + ":" + nameof(FilesystemStorageOptions.Root);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _rootPrefix;
    private readonly int _bufferBytes;
    private readonly ILogger _logger;

    private FilesystemObjectStore(string root, int bufferBytes, int sweptTemporaryFiles, ILogger logger)
    {
        Root = root;
        _rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        _bufferBytes = bufferBytes;
        SweptTemporaryFiles = sweptTemporaryFiles;
        _logger = logger;
    }

    /// <summary>
    /// The capabilities of this provider: create-only publication is native
    /// (a move without overwrite on Windows, a hard link on Linux); no signed
    /// URLs and no ranged reads.
    /// </summary>
    public static StorageCapabilities DeclaredCapabilities => new(
        SignedUpload: false, SignedDownload: false, NativeCreateOnly: true, RangeReads: false);

    /// <inheritdoc />
    public StorageCapabilities Capabilities => DeclaredCapabilities;

    /// <summary>The canonical root directory, as resolved at start.</summary>
    public string Root { get; }

    /// <summary>How many stale temporary files the start-up sweep removed.</summary>
    public int SweptTemporaryFiles { get; }

    /// <summary>
    /// Opens the store: validates <paramref name="options"/>, resolves the root
    /// against <paramref name="contentRootPath"/> (creating it only when
    /// allowed), resolves a link of the root itself once, proves that a file
    /// can be written and published there, and sweeps stale temporary files.
    /// Throws <see cref="StorageConfigurationException"/> naming the setting —
    /// never its value — when the store cannot start.
    /// </summary>
    public static FilesystemObjectStore Open(
        FilesystemStorageOptions options,
        string contentRootPath,
        TimeProvider timeProvider,
        ILogger<FilesystemObjectStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (options.Validate().FirstOrDefault() is { } problem)
        {
            throw new StorageConfigurationException($"{FilesystemStorageOptions.SectionPath}:{problem.Setting}", problem.Reason);
        }

        var root = Path.GetFullPath(options.Root!, contentRootPath);
        if (File.Exists(root))
        {
            throw new StorageConfigurationException(RootSetting, "is not a directory");
        }

        if (!Directory.Exists(root))
        {
            if (!options.CreateIfMissing)
            {
                throw new StorageConfigurationException(RootSetting, "the directory does not exist");
            }

            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new StorageConfigurationException(RootSetting, "the directory cannot be created");
            }
        }

        if (new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            root = target.FullName;
        }

        root = Path.TrimEndingDirectorySeparator(root);
        ProbeWritable(root);
        var swept = CleanupTemporaryFiles.Sweep(root, timeProvider.GetUtcNow(), StaleTemporaryFileAge);
        return new FilesystemObjectStore(root, options.BufferBytes, swept, logger);
    }

    /// <inheritdoc />
    public async Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken)
    {
        RequireKey(key);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content must be readable.", nameof(content));
        }

        string? temporaryPath = null;
        var readingContent = false;
        try
        {
            var path = Resolve(key);
            if (Directory.Exists(path))
            {
                throw KeyIsDirectory();
            }

            if (File.Exists(path))
            {
                return Fail<ObjectWrite>("write", StorageOutcome.AlreadyExists, new StorageError(
                    StorageErrorCodes.AlreadyExists, "An object already exists at the key; it was left unchanged."));
            }

            var directory = Path.GetDirectoryName(path)!;
            EnsureDirectory(directory);
            temporaryPath = Path.Combine(directory, CleanupTemporaryFiles.NameFor(Path.GetFileName(path), RandomNumberGenerator.GetHexString(32, lowercase: true)));

            long size;
            string sha256;
            var buffer = ArrayPool<byte>.Shared.Rent(_bufferBytes);
            try
            {
                await using var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0);
                await using var hashing = new HashingWriteStream(file, leaveOpen: true);
                while (true)
                {
                    readingContent = true;
                    var read = await content.ReadAsync(buffer.AsMemory(0, _bufferBytes), cancellationToken).ConfigureAwait(false);
                    readingContent = false;
                    if (read == 0)
                    {
                        break;
                    }

                    await hashing.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await hashing.CompleteAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                size = hashing.Size;
                sha256 = hashing.Sha256;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (!CreateOnlyPublish.TryPublish(temporaryPath, path))
            {
                return Fail<ObjectWrite>("write", StorageOutcome.AlreadyExists, new StorageError(
                    StorageErrorCodes.AlreadyExists, "An object was published at the key by another writer first; it was left unchanged."));
            }

            return StorageResult.Success(new ObjectWrite(key, size, sha256, ProviderVersion: null));
        }
        catch (Exception exception) when (!readingContent && Translate(exception) is { } failure)
        {
            return Fail<ObjectWrite>("write", failure);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }
        }
    }

    /// <inheritdoc />
    public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken)
    {
        RequireKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSize);
        if (!Sha256Text.IsValid(expectedSha256))
        {
            throw new ArgumentException("The expected SHA-256 must be 64 lower-case hexadecimal characters.", nameof(expectedSha256));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = Resolve(key);
            if (Directory.Exists(path))
            {
                throw KeyIsDirectory();
            }

            if (!File.Exists(path))
            {
                return Task.FromResult(NotFound<VerifiedRead>("read"));
            }

            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferBytes, FileOptions.SequentialScan);
            return Task.FromResult(StorageResult.Success(new VerifiedRead(file, expectedSize, expectedSha256)));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult(NotFound<VerifiedRead>("read"));
        }
        catch (Exception exception) when (Translate(exception) is { } failure)
        {
            return Task.FromResult(Fail<VerifiedRead>("read", failure));
        }
    }

    /// <inheritdoc />
    public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        RequireKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = Resolve(key);
            if (Directory.Exists(path))
            {
                throw KeyIsDirectory();
            }

            var info = new FileInfo(path);
            return Task.FromResult(info.Exists
                ? StorageResult.Success(new ObjectInfo(key, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)))
                : NotFound<ObjectInfo>("info"));
        }
        catch (Exception exception) when (Translate(exception) is { } failure)
        {
            return Task.FromResult(Fail<ObjectInfo>("info", failure));
        }
    }

    /// <inheritdoc />
    public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        RequireKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = Resolve(key);
            if (Directory.Exists(path))
            {
                throw KeyIsDirectory();
            }

            if (!File.Exists(path))
            {
                return Task.FromResult(StorageResult.Success(false));
            }

            File.Delete(path);
            return Task.FromResult(StorageResult.Success(true));
        }
        catch (Exception exception) when (Translate(exception) is { } failure)
        {
            return Task.FromResult(Fail<bool>("delete", failure));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in ListPagesAsync(classPrefix, cancellationToken).ConfigureAwait(false))
        {
            foreach (var item in page)
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// The listing of <paramref name="classPrefix"/> in pages of at most
    /// <see cref="ListPageSize"/> objects, in ordinal key order. Each directory
    /// is read and sorted on its own (its names, a directory's with a trailing
    /// <c>/</c>, which gives the keys' ordinal order), so memory is bounded by
    /// the widest directory and a page, never by the whole listing. Links,
    /// temporary files and names outside the key grammar are skipped.
    /// </summary>
    internal async IAsyncEnumerable<IReadOnlyList<ObjectInfo>> ListPagesAsync(string classPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!ObjectKey.IsValidPrefix(classPrefix, out var reason))
        {
            throw new InvalidObjectKeyException(reason);
        }

        string directory;
        try
        {
            directory = Resolve(ObjectKey.Create(classPrefix));
        }
        catch (StorageException exception)
        {
            LogFailure(_logger, "list", exception.Error.Code);
            throw;
        }

        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var page = new List<ObjectInfo>(ListPageSize);
        var pending = new Stack<IEnumerator<(FileSystemInfo Entry, string Key)>>();
        pending.Push(ReadDirectory(directory, classPrefix).GetEnumerator());
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Peek();
                if (!current.MoveNext())
                {
                    pending.Pop().Dispose();
                    continue;
                }

                var (entry, key) = current.Current;
                if (entry is DirectoryInfo)
                {
                    pending.Push(ReadDirectory(entry.FullName, key).GetEnumerator());
                    continue;
                }

                page.Add(new ObjectInfo(ObjectKey.Create(key), ((FileInfo)entry).Length, new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero)));
                if (page.Count == ListPageSize)
                {
                    yield return page;
                    page = new List<ObjectInfo>(ListPageSize);
                    await Task.Yield();
                }
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                pending.Pop().Dispose();
            }
        }

        if (page.Count > 0)
        {
            yield return page;
        }
    }

    // One directory's entries that can be objects or hold objects, sorted so that keys come out in ordinal order.
    private IEnumerable<(FileSystemInfo Entry, string Key)> ReadDirectory(string directory, string keyPrefix)
    {
        List<(FileSystemInfo Entry, string Key, string SortName)> entries;
        try
        {
            entries = [.. new DirectoryInfo(directory)
                .EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, MatchType = MatchType.Simple })
                .Where(e => e.LinkTarget is null && ObjectKey.TryCreate(keyPrefix + ObjectKey.Separator + e.Name, out _, out _))
                .Select(e => (Entry: e, Key: keyPrefix + ObjectKey.Separator + e.Name, SortName: e is DirectoryInfo ? e.Name + ObjectKey.Separator : e.Name))];
        }
        catch (Exception exception) when (Translate(exception) is { } failure)
        {
            LogFailure(_logger, "list", failure.Error.Code);
            throw failure;
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.SortName, right.SortName));
        return entries.Select(e => (e.Entry, e.Key));
    }

    // The full path of a key, checked: under the root, through every existing link, and not too long.
    private string Resolve(ObjectKey key)
    {
        var relative = key.ToString().Replace(ObjectKey.Separator, Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(_rootPrefix, PathComparison))
        {
            throw OutsideRoot();
        }

        if (path.Length > MaxPathLength)
        {
            throw new StorageException(StorageOutcome.Permanent, new StorageError(
                StorageErrorCodes.PathTooLong, string.Create(CultureInfo.InvariantCulture, $"The key resolves to a path longer than {MaxPathLength} characters under the storage root.")));
        }

        var current = Root;
        foreach (var segment in key.Segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null)
            {
                if (!info.Exists)
                {
                    break;
                }

                continue;
            }

            if (!IsUnderRoot(FinalTarget(info, current)))
            {
                throw OutsideRoot();
            }
        }

        return path;
    }

    private static string FinalTarget(FileSystemInfo link, string linkPath)
    {
        try
        {
            if (link.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                return Path.GetFullPath(target.FullName);
            }
        }
        catch (IOException)
        {
            // A link cycle or an unreadable link: treated as leaving the root.
            return string.Empty;
        }

        return Path.GetFullPath(link.LinkTarget!, Path.GetDirectoryName(linkPath)!);
    }

    private bool IsUnderRoot(string path) =>
        path.Length > 0
        && (string.Equals(Path.TrimEndingDirectorySeparator(path), Root, PathComparison) || path.StartsWith(_rootPrefix, PathComparison));

    // Creates the object's directory; a leading part of the key that is a file makes the key unusable.
    private void EnsureDirectory(string directory)
    {
        for (var current = directory; current.Length > Root.Length; current = Path.GetDirectoryName(current)!)
        {
            if (File.Exists(current))
            {
                throw new StorageException(StorageOutcome.Permanent, new StorageError(
                    StorageErrorCodes.KeyConflict, "A leading part of the key is an object, so the key cannot hold one."));
            }
        }

        Directory.CreateDirectory(directory);
    }

    private static void ProbeWritable(string root)
    {
        // Both names have the temporary-file shape, so a crash between the steps leaves nothing the sweep misses.
        var temporaryPath = Path.Combine(root, CleanupTemporaryFiles.NameFor("start-probe", RandomNumberGenerator.GetHexString(32, lowercase: true)));
        var publishedPath = Path.Combine(root, CleanupTemporaryFiles.NameFor("start-probe", RandomNumberGenerator.GetHexString(32, lowercase: true)));
        try
        {
            using (var probe = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0))
            {
                probe.WriteByte(0x2a);
                probe.Flush(flushToDisk: true);
            }

            CreateOnlyPublish.TryPublish(temporaryPath, publishedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageConfigurationException(RootSetting, "the directory is not writable");
        }
        catch (StorageException exception)
        {
            throw new StorageConfigurationException(RootSetting, exception.Error.Code == StorageErrorCodes.CreateOnlyUnsupported
                ? "the directory's file system cannot publish an object without risking an overwrite"
                : "the directory is not writable");
        }
        finally
        {
            TryDelete(temporaryPath);
            TryDelete(publishedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary file left behind is never visible as an object and is swept at a later start.
        }
    }

    private static void RequireKey(ObjectKey key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("The key is empty.", nameof(key));
        }
    }

    // Maps a provider exception to a failure whose message has no key and no path; null for anything else.
    private static StorageException? Translate(Exception exception) => exception switch
    {
        StorageException storage => storage,
        PathTooLongException => new StorageException(StorageOutcome.Permanent, new StorageError(
            StorageErrorCodes.PathTooLong, "The key resolves to a path the file system cannot hold.")),
        UnauthorizedAccessException => new StorageException(StorageOutcome.Permanent, new StorageError(
            StorageErrorCodes.PermissionDenied, "The storage root does not allow the operation.")),
        IOException io => new StorageException(StorageOutcome.Transient, new StorageError(
            StorageErrorCodes.IoError, string.Create(CultureInfo.InvariantCulture, $"The operation failed with an input/output error ({io.GetType().Name}, 0x{io.HResult:x8})."))),
        _ => null,
    };

    private static StorageException KeyIsDirectory() => new(StorageOutcome.Permanent, new StorageError(
        StorageErrorCodes.KeyIsDirectory, "The key names a directory, not an object."));

    private static StorageException OutsideRoot() => new(StorageOutcome.Permanent, new StorageError(
        StorageErrorCodes.PathOutsideRoot, "The key resolves outside the storage root."));

    private StorageResult<T> NotFound<T>(string operation) => Fail<T>(operation, StorageOutcome.NotFound, new StorageError(
        StorageErrorCodes.NotFound, "No object exists at the key."));

    private StorageResult<T> Fail<T>(string operation, StorageException failure) => Fail<T>(operation, failure.Outcome, failure.Error);

    private StorageResult<T> Fail<T>(string operation, StorageOutcome outcome, StorageError error)
    {
        LogFailure(_logger, operation, error.Code);
        return StorageResult.Failure<T>(outcome, error);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Object storage {Operation} failed: {Code}")]
    private static partial void LogFailure(ILogger logger, string operation, string code);
}
