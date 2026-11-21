using System.Security.Cryptography;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// What one decompressed block is measured by, in the same pass that consumes
/// it: its length, its SHA-256 — the identity of the content a snapshot is fixed
/// by — and its CRC-32, which the compressed object's own trailer has to agree
/// with. The count is checked against the decompressed-size limit as the bytes
/// leave the decompressor, so content that expands far past its stored size is
/// refused at the limit rather than after it. Nothing is buffered and nothing is
/// rewritten: every block is appended once, to all three.
/// </summary>
internal sealed class HashingCountingSink
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Crc32 _crc = new();
    private readonly long _limit;
    private long _count;
    private string? _sha256;
    private Exception? _failure;

    /// <summary>Counts and hashes content whose decompressed size may not exceed <paramref name="limit"/>.</summary>
    /// <param name="limit">The decompressed-size limit, in bytes.</param>
    public HashingCountingSink(long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
    }

    /// <summary>The content bytes counted so far.</summary>
    public long Count => _count;

    /// <summary>The CRC-32 of the content appended so far.</summary>
    public uint Crc32Value => _crc.Value;

    /// <summary>The first failure this sink recorded, when it recorded one.</summary>
    public Exception? RecordedFailure => _failure;

    /// <summary>The SHA-256 of the content, available once <see cref="Complete"/> ran.</summary>
    public string Sha256 => _sha256
        ?? throw new InvalidOperationException("the content has not been consumed to its end yet");

    /// <summary>
    /// Appends <paramref name="block"/>, or refuses when it would cross the
    /// limit: the count stops at the limit and the failure is recorded before it
    /// is raised, so the caller can read which side failed.
    /// </summary>
    /// <param name="block">The block that left the decompressor.</param>
    public void Append(ReadOnlySpan<byte> block)
    {
        var remaining = _limit - _count;
        if (block.Length > remaining)
        {
            if (remaining > 0)
            {
                Measure(block[..(int)remaining]);
                _count = _limit;
            }

            var failure = new UncompressedQuotaExceededException(_limit);
            _failure = failure;
            throw failure;
        }

        Measure(block);
        _count += block.Length;
    }

    /// <summary>Records a failure that happened elsewhere on this side of the read.</summary>
    /// <param name="failure">The failure to record.</param>
    public void Record(Exception failure) => _failure ??= failure;

    /// <summary>Closes the computation and makes <see cref="Sha256"/> available.</summary>
    public void Complete() => _sha256 ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

    private void Measure(ReadOnlySpan<byte> block)
    {
        _hash.AppendData(block);
        _crc.Append(block);
    }
}
