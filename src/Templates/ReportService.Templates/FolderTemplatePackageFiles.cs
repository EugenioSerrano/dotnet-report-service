using System.Text;

namespace ReportService.Templates;

/// <summary>
/// The files of one taken package, held in a folder: the shape a worker receives
/// after it has verified and unpacked the package by its hash and size. Every
/// name is resolved inside that folder and nothing outside it can be reached.
/// </summary>
public sealed class FolderTemplatePackageFiles(string folder) : ITemplatePackageFiles
{
    private readonly string root = Path.GetFullPath(folder);

    /// <inheritdoc />
    public bool Exists(string path)
    {
        var full = Resolve(path);
        return File.Exists(full);
    }

    /// <inheritdoc />
    public Stream OpenRead(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            throw new TemplatePackageException($"The package holds no file named '{path}'.");
        }

        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    }

    /// <inheritdoc />
    public string ReadAllText(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Resolves a package-relative name to a path inside the package, refusing a
    /// rooted path, a backslash, an empty segment, a parent segment and anything
    /// that resolves outside the package folder.
    /// </summary>
    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new TemplatePackageException("A package file name is required.");
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            throw new TemplatePackageException($"The package file name '{path}' uses a backslash.");
        }

        if (Path.IsPathRooted(path) || path.StartsWith('/') || (path.Length > 1 && path[1] == ':'))
        {
            throw new TemplatePackageException($"The package file name '{path}' is an absolute path.");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new TemplatePackageException($"The package file name '{path}' is not a path inside the package.");
            }
        }

        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new TemplatePackageException($"The package file name '{path}' resolves outside the package.");
        }

        return full;
    }
}
