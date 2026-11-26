namespace ReportService.Data.Partitioning;

/// <summary>
/// Where the streaming partitioner writes a block document. It opens a fresh writable
/// stream for each block index and names the key the block is written under; the
/// partitioner writes the block document to the stream and closes it when the block is
/// complete. The sink decides where the bytes live — an in-memory buffer, a discard, or a
/// durable object — and the partitioner computes the block's size and digest from the
/// bytes it writes, never from the sink.
/// </summary>
public interface IBlockSink
{
    /// <summary>Opens the block at <paramref name="blockIndex"/> for writing and names its key.</summary>
    /// <param name="blockIndex">The block's position in the partition, from 0.</param>
    /// <param name="key">The key the block document is written under.</param>
    /// <returns>A fresh writable stream the block document is written to.</returns>
    Stream OpenBlock(int blockIndex, out string key);
}
