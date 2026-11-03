namespace ReportService.TemplatePackages.Tests;

/// <summary>A temporary directory of a test's own, removed when the test ends.</summary>
public sealed class TestDirectory : IDisposable
{
    /// <summary>Creates a directory unique to this instance.</summary>
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "report-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory.</summary>
    public string Path { get; }

    /// <summary>Writes a file under the directory, creating its parent directories.</summary>
    /// <param name="relativePath">The file's path relative to the directory.</param>
    /// <param name="content">The bytes to write.</param>
    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
