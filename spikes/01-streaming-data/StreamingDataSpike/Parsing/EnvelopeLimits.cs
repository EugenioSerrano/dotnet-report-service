using System.Numerics;

namespace StreamingDataSpike.Parsing;

/// <summary>
/// The configurable limits of the incremental reader. The defaults are the
/// starting hypotheses that the measurements test: 1 MiB header, 1 MiB record,
/// 1 MiB token, depth 64, no input limit, a 4 KiB first buffer and blocks of
/// up to 1 000 records or 8 MiB.
/// </summary>
public sealed record EnvelopeLimits
{
    /// <summary>Largest header object, in bytes.</summary>
    public long HeaderLimit { get; init; } = 1_048_576;

    /// <summary>Largest record object, in bytes.</summary>
    public long RecordLimit { get; init; } = 1_048_576;

    /// <summary>Largest single JSON token (string, number or property name), in bytes.</summary>
    public long TokenLimit { get; init; } = 1_048_576;

    /// <summary>Deepest nesting of the document; the root object is depth 1.</summary>
    public int DepthLimit { get; init; } = 64;

    /// <summary>Most uncompressed bytes read from the input; 0 means unlimited.</summary>
    public long InputLimit { get; init; }

    /// <summary>Size of the first read buffer, in bytes (minimum 16).</summary>
    public int InitialBuffer { get; init; } = 4_096;

    /// <summary>Most records per block.</summary>
    public int BlockRecords { get; init; } = 1_000;

    /// <summary>Most record bytes per block.</summary>
    public long BlockBytes { get; init; } = 8_388_608;

    /// <summary>
    /// The largest the read buffer may grow to: the larger of the header and
    /// record limits rounded up to a power of two.
    /// </summary>
    public int BufferCap
    {
        get
        {
            var largest = (ulong)Math.Max(HeaderLimit, RecordLimit);
            var rounded = BitOperations.RoundUpToPowerOf2(Math.Max(largest, 16UL));
            return (int)Math.Min(rounded, 1UL << 30);
        }
    }
}
