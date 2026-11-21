using System.Text;
using ReportService.Templates;

namespace ReportService.Templates.Tests.Sample;

/// <summary>
/// The sample read as one block: the header and the records exactly as the composer
/// reads them, the block a sample composes as, its empty twin, and the hard maximum
/// of a block asserted in both directions - at the bound and one record or one byte
/// over it.
/// </summary>
public sealed class SamplePartitionTests
{
    /// <summary>The bytes of one compact record of <paramref name="length"/> characters.</summary>
    private static string Record(int length)
    {
        // {"p":"x"} is nine characters; the padding fills the record to the requested length.
        const string prefix = "{\"p\":\"x";
        const string suffix = "\"}";
        return prefix + new string('p', length - prefix.Length - suffix.Length) + suffix;
    }

    private static byte[] Document(int records, int recordLength = 9) =>
        Encoding.UTF8.GetBytes(
            """{"contractVersion":"1.0","header":{"title":"Invoice","customer":{"name":"Acme"}},"items":["""
            + string.Join(',', Enumerable.Range(0, records).Select(_ => Record(recordLength)))
            + "]}");

    [Fact]
    public async Task TheSampleIsOneBlockWithItsHeaderAndItsRecords()
    {
        var read = await SamplePartition.ReadAsync(Document(3), TestContext.Current.CancellationToken);

        var sample = Assert.IsType<SampleRead.Partitioned>(read).Sample;
        Assert.Equal("Invoice", sample.Header["title"]);
        Assert.Equal(3, sample.Records.Count);
        Assert.Equal([1L, 2L, 3L], sample.Records.Select(record => record.Ordinal));
        Assert.Equal(0, sample.Block.Index);
        Assert.Equal(3, sample.Block.RecordCount);
        Assert.Equal(1, sample.Block.FirstRecordOrdinal);
        Assert.True(sample.Block.IsFirst);
        Assert.True(sample.Block.IsLast);
    }

    [Fact]
    public async Task TheRecordsAreReadAsTheComposerReadsThem()
    {
        var read = await SamplePartition.ReadAsync(Document(2), TestContext.Current.CancellationToken);

        var sample = Assert.IsType<SampleRead.Partitioned>(read).Sample;
        Assert.Equal("x", sample.Records[0].Values["p"]);
    }

    [Fact]
    public async Task TheEmptySampleKeepsTheHeaderAndHoldsNoRecord()
    {
        var read = await SamplePartition.ReadAsync(Document(3), TestContext.Current.CancellationToken);

        var empty = Assert.IsType<SampleRead.Partitioned>(read).Sample.Empty();

        Assert.Equal("Invoice", empty.Header["title"]);
        Assert.Empty(empty.Records);
        Assert.Equal(0, empty.Block.RecordCount);
        Assert.Equal(1, empty.Block.FirstRecordOrdinal);
        Assert.True(empty.Block.IsFirst);
        Assert.True(empty.Block.IsLast);
    }

    [Fact]
    public async Task TwoThousandRecordsAreOneBlockAndTwoThousandAndOneAreOverTheBound()
    {
        var atTheBound = await SamplePartition.ReadAsync(Document(2_000), TestContext.Current.CancellationToken);
        var overTheBound = await SamplePartition.ReadAsync(Document(2_001), TestContext.Current.CancellationToken);

        Assert.IsType<SampleRead.Partitioned>(atTheBound);
        var over = Assert.IsType<SampleRead.OverBound>(overTheBound);
        Assert.Equal(SampleBound.Records, over.Bound);
        Assert.Equal(2_001, over.Records);
        Assert.Equal(2_001L * 9, over.RecordBytes);
    }

    [Fact]
    public async Task OneMebibyteOfRecordBytesIsOneBlockAndOneByteMoreIsOverTheBound()
    {
        // 1 024 records of 1 024 characters each are exactly 1 048 576 bytes of record content.
        var atTheBound = await SamplePartition.ReadAsync(Document(1_024, recordLength: 1_024), TestContext.Current.CancellationToken);
        var overTheBound = await SamplePartition.ReadAsync(Document(1_025, recordLength: 1_024), TestContext.Current.CancellationToken);

        Assert.IsType<SampleRead.Partitioned>(atTheBound);
        var over = Assert.IsType<SampleRead.OverBound>(overTheBound);
        Assert.Equal(SampleBound.RecordBytes, over.Bound);
        Assert.Equal(1_025, over.Records);
        Assert.Equal((1_025L * 1_024), over.RecordBytes);
    }

    [Fact]
    public async Task ASampleOverBothBoundsNamesTheRecordCount()
    {
        // 2 001 records of 1 024 bytes are over both; the partitioner tests the count first.
        var over = Assert.IsType<SampleRead.OverBound>(await SamplePartition.ReadAsync(Document(2_001, recordLength: 1_024), TestContext.Current.CancellationToken));

        Assert.Equal(SampleBound.Records, over.Bound);
        Assert.Equal(2_001, over.Records);
    }

    [Fact]
    public async Task ASampleWithNoHeaderIsUnreadableAndNotAnException()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"contractVersion":"1.0","items":[]}""");

        var read = await SamplePartition.ReadAsync(bytes, TestContext.Current.CancellationToken);

        var unreadable = Assert.IsType<SampleRead.Unreadable>(read);
        Assert.False(string.IsNullOrWhiteSpace(unreadable.Reason));
    }
}
