namespace ReportService.TemplatePackages.Packages;

/// <summary>
/// One file of a package on its way into the archive: where it sits in the
/// package and how to open its bytes. The bytes are opened as a stream and are
/// never held whole, so a package larger than memory is written as it is read.
/// </summary>
/// <param name="RelativePath">The package-relative path of the file.</param>
/// <param name="OpenAsync">Opens the file's bytes; the caller disposes the stream.</param>
public sealed record PackageFileSource(string RelativePath, Func<CancellationToken, Task<Stream>> OpenAsync)
{
    /// <summary>The file at <paramref name="relativePath"/> of the directory <paramref name="root"/>.</summary>
    /// <param name="root">The directory the package was copied to.</param>
    /// <param name="relativePath">The package-relative path of the file.</param>
    public static PackageFileSource FromFile(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return new PackageFileSource(
            relativePath,
            _ => Task.FromResult<Stream>(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read)));
    }
}
