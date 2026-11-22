using System.Diagnostics;

namespace ReportService.Storage.Tests;

/// <summary>
/// Measures the peak working-set growth of a single non-seekable write to an
/// object store, so a store that streams its source is told apart from one that
/// holds it whole. The hermetic conformance twin calls it over small sources;
/// the on-demand tier calls it over a one-gibibyte source. It samples the
/// process working set on a background thread while the write runs, then removes
/// the object it wrote.
/// </summary>
public static class ObjectStoreMemoryProbe
{
    /// <summary>
    /// Writes a generated source of <paramref name="length"/> bytes at
    /// <paramref name="key"/> through <paramref name="store"/>, returning the
    /// peak working-set growth over the pre-write baseline and the time the
    /// write took. The object is deleted before returning.
    /// </summary>
    public static async Task<(long Growth, TimeSpan Elapsed)> MeasureWriteAsync(
        IObjectStore store,
        string key,
        long length,
        CancellationToken cancellationToken)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = Environment.WorkingSet;
        var peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Factory.StartNew(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    peak = Math.Max(peak, Environment.WorkingSet);
                    Thread.Sleep(2);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var elapsed = Stopwatch.StartNew();
        var result = await store.WriteAsync(ObjectKey.Create(key), new GeneratedContent(length, seed: 18), cancellationToken);
        elapsed.Stop();
        await stop.CancelAsync();
        await sampler;
        peak = Math.Max(peak, Environment.WorkingSet);

        Assert.Equal(StorageOutcome.Success, result.Outcome);
        Assert.Equal(length, result.Value!.Size);
        await store.DeleteAsync(ObjectKey.Create(key), cancellationToken);
        return (peak - baseline, elapsed.Elapsed);
    }
}
