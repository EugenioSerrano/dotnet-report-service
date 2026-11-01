using System.Globalization;

namespace ReportService.Api.Tests;

/// <summary>A directory unique to one test, removed when the test ends.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        var name = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "report-service-tests", name);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relativePath, string content)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
