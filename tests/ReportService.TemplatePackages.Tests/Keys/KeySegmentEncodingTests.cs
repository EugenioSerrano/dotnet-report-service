using ReportService.TemplatePackages.Keys;

namespace ReportService.TemplatePackages.Tests.Keys;

/// <summary>
/// How a caller's value becomes one key segment: an ordinary value is kept as
/// it is, everything else becomes a fixed-length hash, and the two ranges never
/// meet, so no two values share a segment.
/// </summary>
public sealed class KeySegmentEncodingTests
{
    [Theory]
    [InlineData("billing-system")]
    [InlineData("invoice-energy")]
    [InlineData("1.1.0")]
    [InlineData("dev-consumer")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]
    public void AnOrdinaryValueIsKeptAsItIs(string value)
    {
        Assert.True(KeySegmentEncoding.IsRaw(value));
        Assert.Equal(value, KeySegmentEncoding.Encode(value));
    }

    [Theory]
    [InlineData("1.0.0+build.5")]
    [InlineData("Consumer_A")]
    [InlineData("con")]
    [InlineData("nul")]
    [InlineData("con.txt")]
    [InlineData("com1.data")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    [InlineData("has space")]
    public void EveryOtherValueBecomesAFixedLengthHash(string value)
    {
        var encoded = KeySegmentEncoding.Encode(value);

        Assert.StartsWith(KeySegmentEncoding.HashedPrefix, encoded, StringComparison.Ordinal);
        Assert.Equal(KeySegmentEncoding.HashedLength, encoded.Length);
        Assert.NotEqual(value, encoded);
    }

    [Fact]
    public void TheTwoRangesNeverMeet()
    {
        var raw = new string('a', KeySegmentEncoding.MaxRawLength);
        var hashed = KeySegmentEncoding.Encode(raw + "a");

        Assert.True(raw.Length < hashed.Length);
        Assert.NotEqual(raw, hashed);
    }

    [Fact]
    public void TheSameValueAlwaysEncodesTheSameWay()
    {
        Assert.Equal(KeySegmentEncoding.Encode("Consumer_A"), KeySegmentEncoding.Encode("Consumer_A"));
        Assert.NotEqual(KeySegmentEncoding.Encode("Consumer_A"), KeySegmentEncoding.Encode("Consumer_B"));
    }

    [Fact]
    public void AnEmptyValueIsRefusedRatherThanEncoded()
    {
        Assert.Throws<ArgumentException>(() => KeySegmentEncoding.Encode(string.Empty));
        Assert.Throws<ArgumentNullException>(() => KeySegmentEncoding.Encode(null));
        Assert.False(KeySegmentEncoding.IsRaw(null));
    }
}
