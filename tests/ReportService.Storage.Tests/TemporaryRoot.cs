using System.Globalization;
using Microsoft.Extensions.Logging;
using ReportService.Testing;

namespace ReportService.Storage.Tests;

/// <summary>
/// A storage root unique to one test, under the system temporary directory,
/// removed when the test ends; with a log capture for the store under test.
/// </summary>
public sealed class TemporaryRoot : IDisposable
{
    /// <summary>Creates the directory.</summary>
    public TemporaryRoot()
    {
        var name = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "report-service-tests", name);
        Directory.CreateDirectory(Path);
        LoggerFactory = new LoggerFactory([Logs]);
    }

    /// <summary>The root directory.</summary>
    public string Path { get; }

    /// <summary>Every line logged through <see cref="LoggerFactory"/>.</summary>
    public LogCapture Logs { get; } = new();

    /// <summary>The logger factory a store under test logs through.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>The full path of <paramref name="key"/> under the root.</summary>
    public string PathOf(ObjectKey key) =>
        System.IO.Path.Combine([Path, .. key.Segments]);

    /// <inheritdoc />
    public void Dispose()
    {
        LoggerFactory.Dispose();
        if (Directory.Exists(Path))
        {
            RemoveLinks(new DirectoryInfo(Path));
            Directory.Delete(Path, recursive: true);
        }
    }

    // A junction is not removed by a recursive delete on Windows; links are unlinked first, never followed.
    private static void RemoveLinks(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry.LinkTarget is not null)
            {
                entry.Delete();
            }
            else if (entry is DirectoryInfo child)
            {
                RemoveLinks(child);
            }
        }
    }
}
