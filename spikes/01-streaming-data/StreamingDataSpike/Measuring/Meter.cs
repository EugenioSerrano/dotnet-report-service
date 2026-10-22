using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;

namespace StreamingDataSpike.Measuring;

/// <summary>
/// Collects the common memory figures of one measured command: peak managed
/// heap (sampled every 1 000 records and at the end), peak process resident
/// memory, allocations, garbage collections and time.
/// </summary>
internal sealed class Meter
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int[] _collectionsAtStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private long _records;

    /// <summary>Highest <see cref="GC.GetTotalMemory(bool)"/> sample.</summary>
    public long ManagedHeapPeakBytes { get; private set; }

    /// <summary>Call once per record; samples the managed heap every 1 000 records.</summary>
    public void OnRecord()
    {
        if (++_records % 1_000 == 0)
        {
            Sample();
        }
    }

    /// <summary>Samples the managed heap now.</summary>
    public void Sample()
    {
        var bytes = GC.GetTotalMemory(false);
        if (bytes > ManagedHeapPeakBytes)
        {
            ManagedHeapPeakBytes = bytes;
        }
    }

    /// <summary>Takes the final sample and returns every figure.</summary>
    public MeterReading Finish()
    {
        Sample();
        var seconds = _clock.Elapsed.TotalSeconds;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var peakWorkingSet = process.PeakWorkingSet64;
        var vmHwm = ReadVmHwm();
        var memoryInfo = GC.GetGCMemoryInfo();
        return new MeterReading
        {
            ManagedHeapPeakBytes = ManagedHeapPeakBytes,
            ProcessPeakRssBytes = vmHwm is { } hwm && hwm != peakWorkingSet ? hwm : peakWorkingSet,
            PeakWorkingSet64Bytes = peakWorkingSet,
            VmHwmBytes = vmHwm,
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes(true),
            Gen0Collections = GC.CollectionCount(0) - _collectionsAtStart[0],
            Gen1Collections = GC.CollectionCount(1) - _collectionsAtStart[1],
            Gen2Collections = GC.CollectionCount(2) - _collectionsAtStart[2],
            GcMode = (GCSettings.IsServerGC ? "server" : "workstation") + "-" + (IsConcurrent() ? "concurrent" : "non-concurrent"),
            TotalAvailableMemoryBytes = memoryInfo.TotalAvailableMemoryBytes,
            Seconds = seconds,
            Os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "other",
            Runtime = RuntimeInformation.FrameworkDescription,
        };
    }

    private static bool IsConcurrent() =>
        AppContext.GetData("System.GC.Concurrent") is not bool concurrent || concurrent;

    /// <summary>Peak resident set size from <c>/proc/self/status</c> on Linux; <see langword="null"/> elsewhere.</summary>
    private static long? ReadVmHwm()
    {
        const string StatusFile = "/proc/self/status";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !File.Exists(StatusFile))
        {
            return null;
        }

        foreach (var line in File.ReadLines(StatusFile))
        {
            if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
            {
                var kilobytes = line["VmHWM:".Length..].Replace("kB", string.Empty, StringComparison.Ordinal).Trim();
                return long.Parse(kilobytes, CultureInfo.InvariantCulture) * 1024;
            }
        }

        return null;
    }
}

/// <summary>The figures of one measured command.</summary>
internal sealed record MeterReading
{
    public long ManagedHeapPeakBytes { get; init; }

    public long ProcessPeakRssBytes { get; init; }

    public long PeakWorkingSet64Bytes { get; init; }

    public long? VmHwmBytes { get; init; }

    public long TotalAllocatedBytes { get; init; }

    public int Gen0Collections { get; init; }

    public int Gen1Collections { get; init; }

    public int Gen2Collections { get; init; }

    public required string GcMode { get; init; }

    public long TotalAvailableMemoryBytes { get; init; }

    public double Seconds { get; init; }

    public required string Os { get; init; }

    public required string Runtime { get; init; }
}
