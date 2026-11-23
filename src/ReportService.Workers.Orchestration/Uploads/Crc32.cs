using System.Buffers.Binary;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// The CRC-32 of the gzip format, computed incrementally: the reflected
/// polynomial <c>0xEDB88320</c>, an initial state of all ones, the result
/// complemented at the end. Eight shared, read-only tables are built once from the
/// polynomial, so the loop consumes eight bytes per step; an instance keeps only
/// its four-byte state, so appending a block costs eight table lookups per eight
/// bytes and nothing is buffered. The algorithm is written from its published
/// definition and uses no package.
/// </summary>
public sealed class Crc32
{
    /// <summary>The reflected polynomial of the gzip format.</summary>
    public const uint Polynomial = 0xEDB88320u;

    /// <summary>The state every computation starts from.</summary>
    public const uint InitialState = 0xFFFFFFFFu;

    /// <summary>How many bytes the loop consumes in one step.</summary>
    private const int SliceBytes = 8;

    /// <summary>How many entries one slice table holds: one per byte value.</summary>
    private const int EntriesPerTable = 256;

    private static readonly uint[] Tables = BuildTables();

    private uint _state = InitialState;

    /// <summary>Appends <paramref name="data"/> to the computation.</summary>
    /// <param name="data">The bytes to append, in the order they are read.</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        var state = _state;
        var tables = Tables;
        var offset = 0;
        while (data.Length - offset >= SliceBytes)
        {
            state ^= BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var next = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            state =
                tables[(7 * EntriesPerTable) + (int)(state & 0xFFu)] ^
                tables[(6 * EntriesPerTable) + (int)((state >> 8) & 0xFFu)] ^
                tables[(5 * EntriesPerTable) + (int)((state >> 16) & 0xFFu)] ^
                tables[(4 * EntriesPerTable) + (int)(state >> 24)] ^
                tables[(3 * EntriesPerTable) + (int)(next & 0xFFu)] ^
                tables[(2 * EntriesPerTable) + (int)((next >> 8) & 0xFFu)] ^
                tables[(1 * EntriesPerTable) + (int)((next >> 16) & 0xFFu)] ^
                tables[(int)(next >> 24)];
            offset += SliceBytes;
        }

        for (; offset < data.Length; offset++)
        {
            state = tables[(int)((state ^ data[offset]) & 0xFFu)] ^ (state >> 8);
        }

        _state = state;
    }

    /// <summary>The CRC-32 of everything appended so far.</summary>
    public uint Value => _state ^ 0xFFFFFFFFu;

    /// <summary>The CRC-32 of <paramref name="data"/> in one call.</summary>
    /// <param name="data">The bytes to compute over.</param>
    public static uint Of(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Append(data);
        return crc.Value;
    }

    /// <summary>
    /// The eight slice tables of the reflected polynomial, built once: the first
    /// shifts the state by one byte, and each later one is derived from the one
    /// before it, which is what lets the loop fold eight bytes in a single step.
    /// </summary>
    private static uint[] BuildTables()
    {
        var tables = new uint[SliceBytes * EntriesPerTable];
        for (var index = 0u; index < EntriesPerTable; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1u) != 0 ? Polynomial ^ (value >> 1) : value >> 1;
            }

            tables[index] = value;
        }

        for (var slice = 1; slice < SliceBytes; slice++)
        {
            for (var index = 0; index < EntriesPerTable; index++)
            {
                var previous = tables[((slice - 1) * EntriesPerTable) + index];
                tables[(slice * EntriesPerTable) + index] = (previous >> 8) ^ tables[previous & 0xFFu];
            }
        }

        return tables;
    }
}
