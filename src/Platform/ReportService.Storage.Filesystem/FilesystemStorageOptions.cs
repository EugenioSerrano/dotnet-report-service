namespace ReportService.Storage.Filesystem;

/// <summary>
/// The settings of the Filesystem provider, in the <c>Storage:Filesystem</c>
/// section: the root directory (required; absolute, or relative to the
/// process's content root), the copy buffer and whether a missing root may be
/// created. None of them is a secret.
/// </summary>
public sealed class FilesystemStorageOptions
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Storage:Filesystem";

    /// <summary>The copy buffer used when no value is set: 64 KiB.</summary>
    public const int DefaultBufferBytes = 64 * 1024;

    /// <summary>The smallest copy buffer accepted.</summary>
    public const int MinBufferBytes = 4 * 1024;

    /// <summary>The largest copy buffer accepted: 1 MiB.</summary>
    public const int MaxBufferBytes = 1024 * 1024;

    /// <summary>The root directory of the objects.</summary>
    public string? Root { get; set; }

    /// <summary>The size of the buffer a write copies through.</summary>
    public int BufferBytes { get; set; } = DefaultBufferBytes;

    /// <summary>Whether a missing root directory is created at start; otherwise a missing root stops the start.</summary>
    public bool CreateIfMissing { get; set; }

    /// <summary>Returns the problems of these settings, named relative to <see cref="SectionPath"/>; empty when valid.</summary>
    public IEnumerable<StorageSettingProblem> Validate()
    {
        if (string.IsNullOrWhiteSpace(Root))
        {
            yield return new StorageSettingProblem(nameof(Root), "is required");
        }

        if (BufferBytes is < MinBufferBytes or > MaxBufferBytes)
        {
            yield return new StorageSettingProblem(nameof(BufferBytes), "must be between 4096 and 1048576");
        }
    }
}
