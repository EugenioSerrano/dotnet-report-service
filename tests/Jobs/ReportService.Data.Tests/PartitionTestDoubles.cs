using System.Security.Cryptography;
using System.Text;
using ReportService.Data;
using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// The doubles and builders the streaming-partitioner cases are written with: an
/// in-memory block sink that keeps every block document it is handed, the snapshot
/// builders with a group key and an amount field, and the small oracles the cases
/// compute their expectations with. The expectations are derived from the acceptance
/// criteria and the governing ADRs, never from the partitioner — the block bytes are
/// hashed here, independently of whatever digest the partitioner records.
/// </summary>
internal static class PartitionFixtures
{
    /// <summary>The field name a snapshot's group key lives under (the manifest's partitioning.groupKey).</summary>
    public const string GroupField = "group";

    /// <summary>The field name the page-totals amount lives under.</summary>
    public const string AmountField = "amount";

    /// <summary>The spec a grouped, amount-bearing partition runs under, with the limits the case wants.</summary>
    public static PartitionSpec GroupedSpec(PartitionLimits limits) =>
        new(GroupField, AmountField, limits);

    /// <summary>The spec a plain, amount-bearing partition runs under (no group key).</summary>
    public static PartitionSpec PlainAmountSpec(PartitionLimits limits) =>
        new(GroupKeyField: null, AmountField, limits);

    /// <summary>A record that carries a sequence, a group key and an amount, exactly as a snapshot holds it.</summary>
    public static string GroupedRecord(int sequence, string group, string amount) =>
        $$"""{"sequence":{{sequence}},"group":"{{group}}","amount":{{amount}}}""";

    /// <summary>A record that carries a sequence and an amount, with no group key.</summary>
    public static string PlainRecord(int sequence, string amount) =>
        $$"""{"sequence":{{sequence}},"amount":{{amount}}}""";

    /// <summary>The SHA-256 of <paramref name="bytes"/>, lower-case hexadecimal — the independent digest oracle.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The partition of <paramref name="records"/> under <paramref name="spec"/>, with the sink that kept the blocks.</summary>
    public static async Task<(StreamingPartition Partition, InMemoryBlockSink Sink)> PartitionAsync(
        string[] records,
        PartitionSpec spec,
        CancellationToken cancellationToken,
        string header = SnapshotBytes.ValidHeader)
    {
        var snapshot = SnapshotBytes.Document(header, records);
        var sink = new InMemoryBlockSink();
        await using var source = new MemoryStream(snapshot, writable: false);
        var partition = await StreamingPartitioner.PartitionAsync(
            HeaderItemsReader.ReadAsync(source, cancellationToken),
            sink,
            spec,
            cancellationToken);
        return (partition, sink);
    }

    /// <summary>Every record of the partition read back, in block order, as the text each one held.</summary>
    public static async Task<List<string>> ReadAllRecordsAsync(InMemoryBlockSink sink, CancellationToken cancellationToken)
    {
        var records = new List<string>();
        foreach (var index in sink.BlockIndices)
        {
            var read = await SnapshotBytes.ReadAllAsync(sink.Block(index), cancellationToken);
            // read[0] is the header (ordinal 0); the records follow.
            for (var i = 1; i < read.Count; i++)
            {
                records.Add(SnapshotBytes.Text(read[i]));
            }
        }

        return records;
    }
}

/// <summary>
/// An in-memory block sink: it hands the partitioner a fresh writable stream for each
/// block index and keeps the bytes the partitioner wrote, so a case reads a block
/// document back, hashes it independently and checks it against the index entry. A
/// pre-seeded sink carries the committed blocks of an earlier attempt into a resume.
/// </summary>
internal sealed class InMemoryBlockSink : IBlockSink
{
    private readonly SortedDictionary<int, MemoryStream> _blocks = new();
    private readonly int _throwFromBlock;

    /// <summary>A sink that keeps every block it is given.</summary>
    public InMemoryBlockSink()
        : this(int.MaxValue)
    {
    }

    /// <summary>A sink that throws when the partitioner opens the block at <paramref name="throwFromBlock"/>, modelling a crash.</summary>
    public InMemoryBlockSink(int throwFromBlock) => _throwFromBlock = throwFromBlock;

    /// <summary>The key prefix the blocks live under.</summary>
    public string KeyPrefix { get; init; } = "block/job-0123456789abcdef0123456789abcdef";

    /// <summary>The block indices held, in order.</summary>
    public IReadOnlyList<int> BlockIndices => [.. _blocks.Keys];

    /// <inheritdoc />
    public Stream OpenBlock(int blockIndex, out string key)
    {
        if (blockIndex >= _throwFromBlock)
        {
            throw new PartitionInterrupted(blockIndex);
        }

        key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{KeyPrefix}/{blockIndex}");
        var stream = new MemoryStream();
        _blocks[blockIndex] = stream;
        return stream;
    }

    /// <summary>The bytes of the block document at <paramref name="blockIndex"/>.</summary>
    public byte[] Block(int blockIndex) => _blocks[blockIndex].ToArray();

    /// <summary>Whether a block document is held for <paramref name="blockIndex"/>.</summary>
    public bool Has(int blockIndex) => _blocks.ContainsKey(blockIndex);

    /// <summary>Seeds the committed blocks of an earlier attempt, so a resume does not rewrite them.</summary>
    public void Seed(InMemoryBlockSink committed, int throughBlockIndex)
    {
        foreach (var index in committed.BlockIndices)
        {
            if (index <= throughBlockIndex)
            {
                _blocks[index] = new MemoryStream(committed.Block(index), writable: false);
            }
        }
    }
}

/// <summary>The sentinel a crashing sink throws, so a case tells a modelled crash from a real failure.</summary>
internal sealed class PartitionInterrupted(int blockIndex) : Exception($"modelled crash opening block {blockIndex}")
{
    /// <summary>The block index the crash was modelled at.</summary>
    public int BlockIndex { get; } = blockIndex;
}
