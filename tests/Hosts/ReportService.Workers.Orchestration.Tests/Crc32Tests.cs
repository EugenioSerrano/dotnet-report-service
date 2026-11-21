using System.Globalization;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The checksum the compressed object's own trailer is compared with: the table
/// implementation against a bit-serial reference written from the polynomial,
/// over every byte value, every bit position, block lengths up to four thousand
/// and ninety-six bytes fed in four append sizes, and a seeded random corpus —
/// plus the two published check values of the gzip format.
/// </summary>
public sealed class Crc32Tests
{
    [Fact]
    public void ThePublishedCheckValuesHold()
    {
        Assert.Equal(0xCBF43926u, Crc32.Of("123456789"u8));
        Assert.Equal(0x00000000u, Crc32.Of([]));
    }

    [Fact]
    public void AnEmptyComputationIsZeroAndARepeatedOneAppends()
    {
        var crc = new Crc32();
        Assert.Equal(0u, crc.Value);

        crc.Append("1234"u8);
        crc.Append("56789"u8);

        Assert.Equal(0xCBF43926u, crc.Value);
    }

    [Fact]
    public void EveryByteValueInEveryBitPositionAgreesWithTheReference()
    {
        for (var value = 0; value < 256; value++)
        {
            var single = new[] { (byte)value };
            Assert.Equal(Reference(single), Crc32.Of(single));

            for (var bit = 0; bit < 8; bit++)
            {
                var pair = new[] { (byte)value, (byte)(1 << bit) };
                Assert.Equal(Reference(pair), Crc32.Of(pair));
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(1024)]
    public void BlockLengthsUpToFourThousandAndNinetySixFedInOneAppendSizeAgree(int append)
    {
        var data = new byte[4096];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)((index * 37) & 0xFF);
        }

        for (var length = 0; length <= data.Length; length++)
        {
            var crc = new Crc32();
            for (var offset = 0; offset < length; offset += append)
            {
                crc.Append(data.AsSpan(offset, Math.Min(append, length - offset)));
            }

            Assert.Equal(Reference(data.AsSpan(0, length)), crc.Value);
        }
    }

    [Fact]
    public void ASeededRandomCorpusAgreesWithTheReference()
    {
        var random = new Random(20261101);
        for (var round = 0; round < 200; round++)
        {
            var data = new byte[random.Next(0, 3000)];
            random.NextBytes(data);
            Assert.Equal(Reference(data), Crc32.Of(data));
        }
    }

    [Fact]
    public void TheChecksumOfOneMegabyteIsTheReferenceValue()
    {
        var data = new byte[1024 * 1024];
        var random = new Random(11);
        random.NextBytes(data);

        var crc = new Crc32();
        crc.Append(data);

        Assert.Equal(Reference(data), crc.Value);
    }

    /// <summary>The same checksum one bit at a time, with no table.</summary>
    private static uint Reference(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
