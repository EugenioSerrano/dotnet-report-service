using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;

namespace ReportService.TemplatePackages.Packages;

/// <summary>
/// The taken copy of a version, extracted under a directory of its own. It is
/// handed out only once the whole object was read back and verified, so nothing
/// outside this class can reach half of a package, and disposing it removes
/// every file it extracted.
/// </summary>
public sealed class TakenTemplatePackage : IAsyncDisposable, IDisposable
{
    private readonly List<string> _files;

    internal TakenTemplatePackage(
        string root,
        string scratchDirectory,
        IReadOnlyList<string> files,
        TemplateManifest manifest,
        TemplateVersionRecord record)
    {
        Root = root;
        ScratchDirectory = scratchDirectory;
        _files = [.. files];
        Manifest = manifest;
        Record = record;
    }

    /// <summary>The directory the package was extracted to.</summary>
    public string Root { get; }

    /// <summary>The directory this copy created and will delete.</summary>
    public string ScratchDirectory { get; }

    /// <summary>The package-relative paths of the files, in ordinal order.</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>The typed view of the manifest the version was taken with.</summary>
    public TemplateManifest Manifest { get; }

    /// <summary>The record that made this copy usable.</summary>
    public TemplateVersionRecord Record { get; }

    /// <inheritdoc />
    public void Dispose() => Delete();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Delete();
        return ValueTask.CompletedTask;
    }

    private void Delete()
    {
        try
        {
            if (Directory.Exists(ScratchDirectory))
            {
                Directory.Delete(ScratchDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A directory that cannot be removed costs disk, never correctness.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The stored bytes of a version do not form the package they claim to be: they
/// do not verify against the record's hash and size, or they hold an entry that
/// is not a plain file at a package path. The message names neither the key nor
/// a path, so a refusal never says where a package lives.
/// </summary>
public sealed class TemplatePackageCorruptException : Exception
{
    /// <summary>The stored package could not be read back intact.</summary>
    public TemplatePackageCorruptException()
        : base("the stored template package could not be read back intact")
    {
    }

    /// <summary>The stored package could not be read back intact; <paramref name="innerException"/> says why.</summary>
    /// <param name="innerException">The cause, for the log.</param>
    public TemplatePackageCorruptException(Exception innerException)
        : base("the stored template package could not be read back intact", innerException)
    {
    }
}

/// <summary>
/// The stored package could not be read at all — the store was unavailable, or
/// the object is missing. It says what the store reported and nothing about the
/// key.
/// </summary>
public sealed class TemplatePackageUnavailableException : Exception
{
    /// <summary>The store did not deliver the object.</summary>
    /// <param name="outcome">What the store reported.</param>
    public TemplatePackageUnavailableException(StorageOutcome outcome)
        : base($"the stored template package could not be read ({outcome})")
    {
        Outcome = outcome;
    }

    /// <summary>What the store reported.</summary>
    public StorageOutcome Outcome { get; }
}
