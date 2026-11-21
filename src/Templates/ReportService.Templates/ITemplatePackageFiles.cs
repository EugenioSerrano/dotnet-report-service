namespace ReportService.Templates;

/// <summary>
/// Reads a file of a taken template package by its package-relative path. A name
/// outside the package — a parent segment, an absolute path, a backslash or a
/// name that resolves elsewhere — is refused, so a package can never read the
/// host.
/// </summary>
public interface ITemplatePackageFiles
{
    /// <summary>Whether the package holds <paramref name="path"/>.</summary>
    bool Exists(string path);

    /// <summary>Opens <paramref name="path"/> for reading; the caller disposes it.</summary>
    Stream OpenRead(string path);

    /// <summary>Reads <paramref name="path"/> as UTF-8 text.</summary>
    string ReadAllText(string path);
}

/// <summary>A package-relative name that is not inside the package, or a file the package does not hold.</summary>
public sealed class TemplatePackageException(string message) : Exception(message);
