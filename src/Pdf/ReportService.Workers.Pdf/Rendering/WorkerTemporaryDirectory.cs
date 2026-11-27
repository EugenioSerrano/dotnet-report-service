namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// The directory one activity attempt prints into: a directory of its own under the
/// worker's temporary root, removed when the attempt ends, whatever it ended with. A
/// printed file is never written under the storage root before it was verified, and
/// nothing of a failed attempt outlives it.
/// </summary>
public sealed class WorkerTemporaryDirectory : IDisposable
{
    private WorkerTemporaryDirectory(string root, string path)
    {
        Root = root;
        Path = path;
    }

    /// <summary>The root every attempt's directory is created under.</summary>
    public string Root { get; }

    /// <summary>This attempt's directory.</summary>
    public string Path { get; }

    /// <summary>The worker's default temporary root.</summary>
    public static string DefaultRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "report-service-render");

    /// <summary>Creates a directory of this attempt's own.</summary>
    /// <param name="root">The root to create it under, or <see langword="null"/> for the default.</param>
    public static WorkerTemporaryDirectory Create(string? root = null)
    {
        var resolved = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
        var path = System.IO.Path.Combine(resolved, "render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new WorkerTemporaryDirectory(resolved, path);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
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
