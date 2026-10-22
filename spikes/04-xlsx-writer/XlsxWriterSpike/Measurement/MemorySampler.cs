using System.Diagnostics;

namespace XlsxWriterSpike.Measurement;

/// <summary>
/// Samples the managed heap in use and the size of the run's temporary
/// directory every 50 ms on a background thread. The managed heap figure is
/// <see cref="GC.GetTotalMemory(bool)"/> without a collection: it includes
/// garbage not yet collected and can miss a peak shorter than the interval, so
/// it is always read together with allocated bytes, gen2 collections and the
/// operating system's exact peak RSS.
/// </summary>
internal sealed class MemorySampler : IDisposable
{
    private const int IntervalMilliseconds = 50;

    private readonly string? _tempDirectory;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private long _heapPeak;
    private long _tempPeak;

    /// <summary>Starts sampling.</summary>
    /// <param name="tempDirectory">Directory whose total file size is sampled, or <c>null</c> to not measure temporary disk.</param>
    public MemorySampler(string? tempDirectory)
    {
        _tempDirectory = tempDirectory;
        _thread = new Thread(Loop) { IsBackground = true, Name = "memory-sampler" };
        _thread.Start();
    }

    /// <summary>Largest managed heap sample, in bytes.</summary>
    public long HeapPeakBytes => Interlocked.Read(ref _heapPeak);

    /// <summary>Largest temporary directory size sample, in bytes; <c>null</c> when not measured.</summary>
    public long? TempPeakBytes => _tempDirectory is null ? null : Interlocked.Read(ref _tempPeak);

    /// <summary>Stops sampling after one last sample.</summary>
    public void Stop()
    {
        _stop.Set();
        _thread.Join();
        Sample();
    }

    /// <summary>The process peak working set (peak RSS on Linux), in bytes.</summary>
    public static long ProcessPeakRssBytes()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PeakWorkingSet64;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_stop.IsSet)
        {
            Stop();
        }

        _stop.Dispose();
    }

    private void Loop()
    {
        do
        {
            Sample();
        }
        while (!_stop.Wait(IntervalMilliseconds));
    }

    private void Sample()
    {
        Max(ref _heapPeak, GC.GetTotalMemory(false));
        if (_tempDirectory is not null)
        {
            Max(ref _tempPeak, DirectorySize(_tempDirectory));
        }
    }

    private static long DirectorySize(string directory)
    {
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    total += file.Length;
                }
                catch (IOException)
                {
                    // The file disappeared between listing and reading its size.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A sample is skipped when the directory changes while it is listed.
        }

        return total;
    }

    private static void Max(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
