using ReportService.Jobs;
using ReportService.Workers.Pdf.Rendering;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The part key class: a single-block output keeps the recorded exception and writes
/// its part under <c>outputs/…</c>; a multi-block output writes every part under
/// <c>parts/…</c> with its block index, so only the assembled PDF is an
/// output-class object.
/// </summary>
public sealed class PartKeyTests
{
    private const string Attempt = "0123456789abcdef01234567";

    [Fact]
    public void ASingleBlockOutputWritesItsPartUnderTheOutputNamespace()
    {
        var partition = Partition(blockCount: 1);

        var key = RenderPdfBlockActivity.KeyOf(partition, Input(0), Attempt);

        Assert.Equal($"outputs/job-1/output-1/{Attempt}", key.ToString());
    }

    [Fact]
    public void AMultiBlockOutputWritesEachPartUnderThePartNamespaceWithItsIndex()
    {
        var partition = Partition(blockCount: 3);

        Assert.Equal($"parts/job-1/0/{Attempt}", RenderPdfBlockActivity.KeyOf(partition, Input(0), Attempt).ToString());
        Assert.Equal($"parts/job-1/2/{Attempt}", RenderPdfBlockActivity.KeyOf(partition, Input(2), Attempt).ToString());
    }

    private static PartitionRecord Partition(int blockCount) => new(
        "job-1",
        2,
        blockCount,
        blockCount * 10,
        blockCount * 100,
        "index-key",
        new string('a', 64),
        1,
        DateTimeOffset.UnixEpoch);

    private static RenderPdfBlockInput Input(int blockIndex) => new("job-1", "output-1", blockIndex);
}
