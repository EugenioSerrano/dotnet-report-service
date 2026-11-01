namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// The CRC-32 of the gzip format, computed incrementally: the reflected
/// polynomial <c>0xEDB88320</c>, an initial state of all ones, the result
/// complemented at the end. One shared, read-only table is built once from the
/// polynomial; an instance keeps only its four-byte state, so appending a block
/// costs one table lookup per byte and nothing is buffered. The algorithm is
/// written from its published definition and uses no package.
/// </summary>
public sealed class Crc32
{
    /// <summary>The reflected polynomial of the gzip format.</summary>
    public const uint Polynomial = 0xEDB88320u;

    /// <summary>The state every computation starts from.</summary>
    public const uint InitialState = 0xFFFFFFFFu;

    private static readonly uint[] Table = BuildTable();

    private uint _state = InitialState;

    /// <summary>Appends <paramref name="data"/> to the computation.</summary>
    /// <param name="data">The bytes to append, in the order they are read.</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        var state = _state;
        foreach (var value in data)
        {
            state = Table[(int)((state ^ value) & 0xFFu)] ^ (state >> 8);
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

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var index = 0u; index < 256u; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1u) != 0 ? Polynomial ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
