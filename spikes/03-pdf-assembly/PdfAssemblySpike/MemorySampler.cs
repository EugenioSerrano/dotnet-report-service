using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace PdfAssemblySpike;

/// <summary>Figures one assembly process reports about itself.</summary>
internal sealed record ProcessMetrics(
    string Variant,
    int Pages,
    double WorkSeconds,
    long ManagedHeapPeakBytes,
    long SampledRssPeakBytes,
    long VmHwmBytes,
    long OutputBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    bool ServerGc,
    int Samples);

/// <summary>
/// Samples the managed heap size and the resident set size of the current
/// process every 20 ms on a background thread and keeps the peaks.
/// </summary>
internal sealed class MemorySampler : IDisposable
{
    private readonly Thread _thread;
    private readonly Stopwatch _clock = new();
    private volatile bool _stop;
    private long _heapPeak;
    private long _rssPeak;
    private int _samples;

    public MemorySampler()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "memory-sampler" };
    }

    public void Start()
    {
        _clock.Start();
        _thread.Start();
    }

    public ProcessMetrics Stop(string variant, int pages, long outputBytes)
    {
        _stop = true;
        _thread.Join();
        _clock.Stop();
        Sample();
        return new ProcessMetrics(
            variant,
            pages,
            _clock.Elapsed.TotalSeconds,
            _heapPeak,
            _rssPeak,
            ReadStatusKb("VmHWM:") * 1024,
            outputBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GCSettings.IsServerGC,
            _samples);
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive)
        {
            _thread.Join();
        }
    }

    private void Loop()
    {
        while (!_stop)
        {
            Sample();
            Thread.Sleep(20);
        }
    }

    private void Sample()
    {
        long heap = GC.GetTotalMemory(false);
        long rss = ReadStatusKb("VmRSS:") * 1024;
        if (rss == 0)
        {
            rss = Environment.WorkingSet;
        }

        if (heap > _heapPeak)
        {
            _heapPeak = heap;
        }

        if (rss > _rssPeak)
        {
            _rssPeak = rss;
        }

        _samples++;
    }

    /// <summary>Reads one "Name: value kB" line of /proc/self/status; 0 when unavailable.</summary>
    private static long ReadStatusKb(string label)
    {
        const string status = "/proc/self/status";
        if (!File.Exists(status))
        {
            return 0;
        }

        foreach (string line in File.ReadLines(status))
        {
            if (line.StartsWith(label, StringComparison.Ordinal))
            {
                string value = line[label.Length..].Trim();
                int space = value.IndexOf(' ', StringComparison.Ordinal);
                return long.Parse(space < 0 ? value : value[..space], CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }
}
