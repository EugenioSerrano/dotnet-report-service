namespace ReportService.Samples.DatasetGenerator;

/// <summary>
/// SplitMix64 pseudo-random generator. Implemented here, instead of using
/// <see cref="Random"/>, so that the sequence for a given seed is a fixed
/// contract that does not depend on the .NET runtime version.
/// </summary>
internal sealed class SplitMix64(ulong seed)
{
    private ulong state = seed;

    /// <summary>Returns the next 64-bit value of the sequence.</summary>
    public ulong Next()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Returns a value between <paramref name="min"/> and <paramref name="maxInclusive"/>.</summary>
    public int NextInt(int min, int maxInclusive) =>
        min + (int)(Next() % (ulong)(maxInclusive - min + 1));

    /// <summary>Returns a value between <paramref name="min"/> and <paramref name="maxInclusive"/>.</summary>
    public long NextLong(long min, long maxInclusive) =>
        min + (long)(Next() % (ulong)(maxInclusive - min + 1));
}
