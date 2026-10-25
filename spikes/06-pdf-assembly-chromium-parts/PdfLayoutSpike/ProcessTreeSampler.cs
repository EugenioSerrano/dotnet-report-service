using System.Globalization;

namespace PdfLayoutSpike;

/// <summary>Peak figures of one sampling window. Memory in KiB unless the name says bytes.</summary>
internal sealed record SampleSummary(
    int Samples,
    long ChromiumTreePeakRssKiB,
    long ChromiumTreePeakPssKiB,
    int ChromiumProcessesMax,
    long DriverPeakRssKiB,
    long HarnessPeakRssKiB,
    long ManagedHeapPeakBytes,
    long TmpDiskPeakBytes);

/// <summary>
/// Samples, every 50 ms, the memory of the harness's process tree from /proc:
/// the Chromium processes (summed RSS and PSS), the Playwright driver and the
/// harness itself, plus the managed heap and, every 500 ms, the size of /tmp.
/// Linux only; elsewhere every figure is reported as not measured.
/// </summary>
internal sealed class ProcessTreeSampler
{
    private const int IntervalMilliseconds = 50;
    private const int TmpEvery = 10;
    private readonly int rootPid = Environment.ProcessId;
    private Thread? thread;
    private volatile bool stopping;
    private int samples;
    private long chromiumRss;
    private long chromiumPss;
    private int chromiumCount;
    private long driverRss;
    private long harnessRss;
    private long managedHeap;
    private long tmpDisk;

    public static bool Supported => OperatingSystem.IsLinux();

    public void Start()
    {
        samples = 0;
        chromiumRss = chromiumPss = driverRss = harnessRss = managedHeap = tmpDisk = 0;
        chromiumCount = 0;
        stopping = false;
        if (!Supported)
        {
            return;
        }

        thread = new Thread(Loop) { IsBackground = true, Name = "process-tree-sampler" };
        thread.Start();
    }

    public SampleSummary Stop()
    {
        stopping = true;
        thread?.Join();
        thread = null;
        if (Supported)
        {
            // One last sample so very short windows still have a figure.
            SampleOnce(measureTmp: true);
        }

        return new SampleSummary(samples, chromiumRss, chromiumPss, chromiumCount, driverRss, harnessRss, managedHeap, tmpDisk);
    }

    /// <summary>VmHWM of the harness: its peak RSS since it started (cannot be reset per run).</summary>
    public static long? HarnessHighWaterMarkKiB() => Supported ? ReadStatusKiB(Environment.ProcessId, "VmHWM:") : null;

    /// <summary>The container cgroup's memory.peak at this moment (running peak), when readable.</summary>
    public static long? CgroupMemoryPeakBytes()
    {
        try
        {
            var text = File.ReadAllText("/sys/fs/cgroup/memory.peak").Trim();
            return long.Parse(text, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    public static long? MemoryTotalKiB() => Supported ? ReadKiB("/proc/meminfo", "MemTotal:") : null;

    /// <summary>The command line of the first Chromium process of the tree, as launched by the driver.</summary>
    public string? ChromiumCommandLine()
    {
        if (!Supported)
        {
            return null;
        }

        foreach (var (pid, _) in Descendants())
        {
            if (Classify(pid) == ProcessKind.Chromium)
            {
                try
                {
                    return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim();
                }
                catch (IOException)
                {
                }
            }
        }

        return null;
    }

    private void Loop()
    {
        var tick = 0;
        while (!stopping)
        {
            SampleOnce(measureTmp: tick % TmpEvery == 0);
            tick++;
            Thread.Sleep(IntervalMilliseconds);
        }
    }

    private void SampleOnce(bool measureTmp)
    {
        long rss = 0;
        long pss = 0;
        var count = 0;
        long driver = 0;
        foreach (var (pid, _) in Descendants())
        {
            var kind = Classify(pid);
            if (kind == ProcessKind.Chromium)
            {
                rss += ReadStatusKiB(pid, "VmRSS:") ?? 0;
                pss += ReadKiB($"/proc/{pid}/smaps_rollup", "Pss:") ?? 0;
                count++;
            }
            else if (kind == ProcessKind.Driver)
            {
                driver += ReadStatusKiB(pid, "VmRSS:") ?? 0;
            }
        }

        var self = ReadStatusKiB(rootPid, "VmRSS:") ?? 0;
        var heap = GC.GetGCMemoryInfo().HeapSizeBytes;
        samples++;
        chromiumRss = Math.Max(chromiumRss, rss);
        chromiumPss = Math.Max(chromiumPss, pss);
        chromiumCount = Math.Max(chromiumCount, count);
        driverRss = Math.Max(driverRss, driver);
        harnessRss = Math.Max(harnessRss, self);
        managedHeap = Math.Max(managedHeap, heap);
        if (measureTmp)
        {
            tmpDisk = Math.Max(tmpDisk, DirectorySize("/tmp"));
        }
    }

    private enum ProcessKind
    {
        Other,
        Chromium,
        Driver,
    }

    /// <summary>
    /// Chromium by process name; the driver by its executable path, because the
    /// driver's process name is the name of its main thread ("MainThread"), not "node".
    /// </summary>
    private static ProcessKind Classify(int pid)
    {
        var comm = ReadComm(pid);
        if (comm is null)
        {
            return ProcessKind.Other;
        }

        if (comm.StartsWith("chrome", StringComparison.Ordinal) || comm.StartsWith("headless_shell", StringComparison.Ordinal))
        {
            return ProcessKind.Chromium;
        }

        var executable = ReadExecutable(pid);
        return executable is not null && executable.Contains("/.playwright/node/", StringComparison.Ordinal) && executable.EndsWith("/node", StringComparison.Ordinal)
            ? ProcessKind.Driver
            : ProcessKind.Other;
    }

    /// <summary>The first argument of the process's command line (its executable as launched).</summary>
    private static string? ReadExecutable(int pid)
    {
        try
        {
            var commandLine = File.ReadAllText($"/proc/{pid}/cmdline");
            var end = commandLine.IndexOf('\0', StringComparison.Ordinal);
            return end >= 0 ? commandLine[..end] : commandLine;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>All descendants of the harness process, from the parent links in /proc/[pid]/stat.</summary>
    private List<(int Pid, int Parent)> Descendants()
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            var parent = ReadParent(pid);
            if (parent is null)
            {
                continue;
            }

            if (!children.TryGetValue(parent.Value, out var list))
            {
                list = [];
                children[parent.Value] = list;
            }

            list.Add(pid);
        }

        var result = new List<(int, int)>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!children.TryGetValue(current, out var list))
            {
                continue;
            }

            foreach (var child in list)
            {
                result.Add((child, current));
                queue.Enqueue(child);
            }
        }

        return result;
    }

    private static int? ReadParent(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')');
            var fields = stat[(close + 2)..].Split(' ');
            return int.Parse(fields[1], CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadComm(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadStatusKiB(int pid, string key) => ReadKiB($"/proc/{pid}/status", key);

    private static long? ReadKiB(string path, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith(key, StringComparison.Ordinal))
                {
                    var parts = line[key.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return long.Parse(parts[0], CultureInfo.InvariantCulture);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
        }

        return null;
    }

    private static long DirectorySize(string path)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                try
                {
                    total += file.Length;
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }
}
