using System.Diagnostics;

namespace MarkdownSpike.Measuring;

/// <summary>
/// Samples the managed heap and the process working set every 10 ms on a
/// dedicated thread, and the bytes under the run's temporary folder every
/// second, keeping the maxima.
/// </summary>
internal sealed class MemorySampler : IDisposable
{
    private readonly Thread thread;
    private readonly Process process = Process.GetCurrentProcess();
    private readonly string? tempFolder;
    private volatile bool stopping;
    private long maxManaged;
    private long maxWorkingSet;
    private long maxTemp;

    public MemorySampler(string? tempFolder)
    {
        this.tempFolder = tempFolder;
        Sample();
        SampleTemp();
        thread = new Thread(Loop) { IsBackground = true, Name = "memory sampler", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public long PeakManagedHeapBytes => maxManaged;

    public long PeakWorkingSetBytes => maxWorkingSet;

    public long TempDiskBytes => maxTemp;

    public void Stop()
    {
        if (stopping)
        {
            return;
        }

        stopping = true;
        thread.Join();
        Sample();
        SampleTemp();
        process.Refresh();
        maxWorkingSet = Math.Max(maxWorkingSet, process.PeakWorkingSet64);
    }

    public void Dispose()
    {
        Stop();
        process.Dispose();
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        long lastTemp = 0;
        while (!stopping)
        {
            Sample();
            if (clock.ElapsedMilliseconds - lastTemp >= 1000)
            {
                SampleTemp();
                lastTemp = clock.ElapsedMilliseconds;
            }

            Thread.Sleep(10);
        }
    }

    private void Sample()
    {
        maxManaged = Math.Max(maxManaged, GC.GetTotalMemory(false));
        process.Refresh();
        maxWorkingSet = Math.Max(maxWorkingSet, process.WorkingSet64);
    }

    private void SampleTemp()
    {
        if (tempFolder is null || !Directory.Exists(tempFolder))
        {
            return;
        }

        long total = 0;
        foreach (FileInfo file in new DirectoryInfo(tempFolder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            total += file.Length;
        }

        maxTemp = Math.Max(maxTemp, total);
    }
}
