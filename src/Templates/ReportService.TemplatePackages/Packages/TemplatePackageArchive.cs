using System.Formats.Tar;

namespace ReportService.TemplatePackages.Packages;

/// <summary>
/// The stored form of a taken package: one uncompressed ustar archive holding
/// one entry per file, in ordinal order of the package-relative path, with
/// every header field a constant. The same files therefore always produce the
/// same bytes on every machine and at every moment, which is what lets the
/// package's hash be its identity.
/// </summary>
public static class TemplatePackageArchive
{
    private const UnixFileMode EntryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Writes the archive of <paramref name="files"/> into <paramref name="sink"/>.</summary>
    /// <param name="files">The files of the package, in any order.</param>
    /// <param name="sink">Where the archive is written; left open.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="ArgumentException">A file's path is not a package path, or two files share one.</exception>
    /// <exception cref="TemplatePackageUnsupportedPathException">The archive format cannot hold a file's path.</exception>
    public static async Task WriteAsync(
        IReadOnlyList<PackageFileSource> files,
        Stream sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sink);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (!PackagePath.IsValid(file.RelativePath, out var reason))
            {
                throw new ArgumentException($"'{file.RelativePath}': {reason}", nameof(files));
            }
        }

        var ordered = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
        for (var index = 1; index < ordered.Count; index++)
        {
            if (string.Equals(ordered[index - 1].RelativePath, ordered[index].RelativePath, StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{ordered[index].RelativePath}': the package holds it twice", nameof(files));
            }
        }

        using var writer = new TarWriter(sink, TarEntryFormat.Ustar, leaveOpen: true);
        foreach (var file in ordered)
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, file.RelativePath)
            {
                ModificationTime = DateTimeOffset.UnixEpoch,
                Mode = EntryMode,
                Uid = 0,
                Gid = 0,
                UserName = string.Empty,
                GroupName = string.Empty,
            };

            await using var content = await file.OpenAsync(cancellationToken).ConfigureAwait(false);
            entry.DataStream = content;
            try
            {
                await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not TemplatePackageUnsupportedPathException)
            {
                throw new TemplatePackageUnsupportedPathException(file.RelativePath, exception);
            }
        }
    }
}

/// <summary>
/// The archive format cannot hold a file's path: a path longer than the format's
/// name and prefix fields, or a stream that cannot report its length. The path
/// is a template author's, so it is named in the message.
/// </summary>
public sealed class TemplatePackageUnsupportedPathException : Exception
{
    /// <summary>The path the archive could not hold.</summary>
    public TemplatePackageUnsupportedPathException(string path, Exception innerException)
        : base($"the package path '{path}' cannot be written into the package archive", innerException)
    {
        Path = path;
    }

    /// <summary>The refused package path.</summary>
    public string Path { get; }
}
