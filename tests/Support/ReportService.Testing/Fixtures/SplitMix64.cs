namespace ReportService.Testing.Fixtures;

/// <summary>
/// SplitMix64. Small, fast and identical on every platform, so a fixture is a
/// function of its id and never of the machine that generated it.
/// </summary>
/// <param name="seed">The seed of this stream.</param>
public sealed class SplitMix64(ulong seed)
{
    private ulong state = seed;

    /// <summary>The next 64-bit value of the stream.</summary>
    public ulong Next()
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>
/// 64-bit FNV-1a over bytes. Never <see cref="string.GetHashCode()"/>, whose
/// value changes from one process to the next.
/// </summary>
public static class Fnv1a
{
    /// <summary>The 64-bit FNV-1a hash of <paramref name="bytes"/>.</summary>
    public static ulong Hash64(ReadOnlySpan<byte> bytes)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 0x100000001B3UL;
        }

        return hash;
    }
}
