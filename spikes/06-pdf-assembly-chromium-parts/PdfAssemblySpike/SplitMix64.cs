namespace PdfAssemblySpike;

/// <summary>
/// SplitMix64 pseudo-random generator. Its algorithm is fixed, so the same seed
/// gives the same sequence on every runtime version, unlike System.Random.
/// </summary>
internal sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform integer in [minInclusive, maxExclusive), by rejection sampling (no modulo bias).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        }

        ulong range = (ulong)((long)maxExclusive - minInclusive);
        ulong threshold = (0UL - range) % range;
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value < threshold);

        return (int)(minInclusive + (long)(value % range));
    }
}
