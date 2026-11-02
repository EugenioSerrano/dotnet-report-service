using System.Formats.Tar;
using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;

namespace ReportService.TemplatePackages.Packages;

/// <summary>
/// Opens the taken copy of a version: a verifying read of the stored object,
/// extracted into a fresh directory that is handed out only after the whole
/// stream was read without an integrity failure. Every entry must be a plain
/// file at a package path, so nothing a package holds can be written outside
/// the directory this class created.
/// </summary>
public sealed class TakenTemplatePackageReader
{
    private readonly IObjectStore _store;

    /// <summary>Reads through <paramref name="store"/>, which the process owns.</summary>
    /// <param name="store">The object store holding taken packages.</param>
    public TakenTemplatePackageReader(IObjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Extracts the package of <paramref name="record"/> under <paramref name="scratchRoot"/>.</summary>
    /// <param name="record">The record naming the object.</param>
    /// <param name="scratchRoot">The directory under which a directory of this call's own is created.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="TemplatePackageUnavailableException">The store did not deliver the object.</exception>
    /// <exception cref="TemplatePackageCorruptException">The bytes do not verify, or hold an entry that is not a package file.</exception>
    public async Task<TakenTemplatePackage> OpenAsync(
        TemplateVersionRecord record,
        string scratchRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(scratchRoot);

        var opened = await _store.OpenReadAsync(record.ObjectKey, record.PackageSize, record.PackageSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!opened.Succeeded || opened.Value is null)
        {
            throw new TemplatePackageUnavailableException(opened.Outcome);
        }

        var directory = Path.Combine(scratchRoot, "taken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var files = new List<string>();
            using var read = opened.Value;
            using var reader = new TarReader(read.Content, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                files.Add(Extract(entry, directory));
            }

            // The whole object must be read: the verifying stream proves the size
            // and the hash only at its end, and this copy is handed out only then.
            await read.Content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);

            return new TakenTemplatePackage(directory, directory, files, record.Manifest, record);
        }
        catch (StorageIntegrityException exception)
        {
            Delete(directory);
            throw new TemplatePackageCorruptException(exception);
        }
        catch (InvalidDataException exception)
        {
            Delete(directory);
            throw new TemplatePackageCorruptException(exception);
        }
        catch
        {
            Delete(directory);
            throw;
        }
    }

    private static string Extract(TarEntry entry, string directory)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            throw new TemplatePackageCorruptException();
        }

        var path = entry.Name;
        if (!PackagePath.IsValid(path) || path.Contains("..", StringComparison.Ordinal))
        {
            throw new TemplatePackageCorruptException();
        }

        var target = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(target);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        if (entry.DataStream is not { } content)
        {
            throw new TemplatePackageCorruptException();
        }

        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        content.CopyTo(output);
        return path;
    }

    private static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
