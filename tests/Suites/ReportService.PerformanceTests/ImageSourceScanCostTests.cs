using System.Diagnostics;
using System.Text;
using ReportService.Templates;

namespace ReportService.PerformanceTests;

/// <summary>
/// The cost of reading every image source while a block is composed: a block close to the
/// 32 MiB guard, made of rows with and without a 1 MiB image data URI, written through the
/// guarded writer. No threshold is set - none is decided for this cost - so the case asserts
/// only that the block is admitted and that the scan holds no copy of the text, and it reports
/// the time and the allocation beside the same writes into a plain sink for comparison.
/// </summary>
[Collection(nameof(MemoryMeasured))]
public sealed class ImageSourceScanCostTests(ITestOutputHelper output)
{
    [Fact]
    public void ABlockNearTheGuardIsReadInOnePassWithoutKeepingTheText()
    {
        var image = "data:image/png;base64," + new string('A', 1024 * 1024);
        var withImage = "<tr><td>1</td><td><img alt=\"x\" src=\"" + image + "\"></td></tr>";
        var textRow = "<tr><td>2</td><td>" + new string('t', 64 * 1024) + "</td></tr>";
        var rows = new List<string>();
        long size = 0;
        while (size < 30L * 1024 * 1024)
        {
            var row = rows.Count % 4 == 0 ? withImage : textRow;
            rows.Add(row);
            size += row.Length;
        }

        var baseline = Measure(rows, scanned: false);
        var scanned = Measure(rows, scanned: true);

        output.WriteLine(
            $"block {size} chars, {rows.Count} writes: plain {baseline.Elapsed.TotalMilliseconds:0} ms / {baseline.Allocated} B; "
            + $"scanned {scanned.Elapsed.TotalMilliseconds:0} ms / {scanned.Allocated} B");

        // The scan keeps no copy of a value or of the block: its own allocation stays far below the text.
        Assert.True(scanned.Allocated - baseline.Allocated < size, "the scan allocated as much as the text it read");
    }

    private static (TimeSpan Elapsed, long Allocated) Measure(IReadOnlyList<string> rows, bool scanned)
    {
        var sink = new DiscardingWriter();
        TextWriter target = scanned ? new GuardedWriter(sink, TimeProvider.System, ImageSourcePolicy.Pdf) : sink;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        foreach (var row in rows)
        {
            if (target is GuardedWriter guarded)
            {
                guarded.BeginCall();
            }

            target.Write(row);
        }

        (target as GuardedWriter)?.Complete();
        watch.Stop();
        return (watch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class DiscardingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string? value)
        {
        }
    }
}
