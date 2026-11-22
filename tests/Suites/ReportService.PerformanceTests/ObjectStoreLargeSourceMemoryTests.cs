using Microsoft.Extensions.Logging;
using ReportService.Storage.Filesystem;
using ReportService.Storage.Tests;

namespace ReportService.PerformanceTests;

/// <summary>
/// The storage provider's large-source bounded-memory case, kept out of the
/// hermetic inner loop: it writes a generated one-gibibyte non-seekable source
/// and compares the process's peak working-set growth with a sixty-four mebibyte
/// write, so a provider that streamed its source is told apart from one that held
/// it whole. It reaches the Filesystem provider and the shared memory probe
/// through the storage conformance test project, the documented way a second
/// project reuses that suite. The hermetic twin over small sources stays in that
/// project so the streaming-write path is covered in every V-Bounce.
/// </summary>
[Collection(nameof(MemoryMeasured))]
public sealed class ObjectStoreLargeSourceMemoryTests : IDisposable
{
    private const long MemoryAllowance = 32L * 1024 * 1024;
    private const long SmallSource = 64L * 1024 * 1024;
    private const long LargeSource = 1024L * 1024 * 1024;

    private readonly TemporaryRoot _root = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AOneGibibyteNonSeekableSourceIsWrittenWithBoundedMemory()
    {
        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = _root.Path },
            _root.Path,
            TimeProvider.System,
            _root.LoggerFactory.CreateLogger<FilesystemObjectStore>());

        var small = await ObjectStoreMemoryProbe.MeasureWriteAsync(store, "memory/small", SmallSource, Cancellation);
        var large = await ObjectStoreMemoryProbe.MeasureWriteAsync(store, "memory/large", LargeSource, Cancellation);

        var figure = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Peak working-set growth: 64 MiB source {small.Growth / 1024.0 / 1024.0:F1} MiB in {small.Elapsed.TotalSeconds:F1} s; " +
            $"1 GiB source {large.Growth / 1024.0 / 1024.0:F1} MiB in {large.Elapsed.TotalSeconds:F1} s; " +
            $"bound: 1 GiB growth <= 64 MiB growth + 32 MiB.");
        TestContext.Current.TestOutputHelper?.WriteLine(figure);
        TestContext.Current.AddAttachment("memory-figure", figure);
        Assert.True(large.Growth <= small.Growth + MemoryAllowance, figure);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _root.Dispose();
        GC.SuppressFinalize(this);
    }
}
