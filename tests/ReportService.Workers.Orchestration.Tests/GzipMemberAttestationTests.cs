using System.Globalization;
using ReportService.Workers.Orchestration.Tests.Uploads;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The decision that a stored compressed object is one complete unit of content:
/// the twenty-byte floor, the trailer taken from the object's own last eight
/// bytes, the length compared modulo 2^32, and the refusal of anything shorter.
/// </summary>
public sealed class GzipMemberAttestationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(19)]
    public void AnObjectUnderTwentyBytesIsNeverAttested(int storedLength)
    {
        // Even with a trailer that would agree, an object too short to hold a header,
        // a compressed stream and a trailer cannot be one complete unit.
        Assert.False(GzipMemberAttestation.IsAttested(GzipMember.Trailer(0, 0), storedLength, 0, 0));
    }

    [Fact]
    public void TheTrailerOfACompleteObjectAttestsTheContentRead()
    {
        var content = GzipMember.Content(512);
        var crc = Crc32.Of(content);
        var trailer = GzipMember.Trailer(crc, content.Length);

        Assert.True(GzipMemberAttestation.IsAttested(trailer, 600, crc, content.Length));
    }

    [Fact]
    public void AChecksumThatDiffersIsNotAttested()
    {
        var content = GzipMember.Content(512);
        var trailer = GzipMember.Trailer(Crc32.Of(content) ^ 1u, content.Length);

        Assert.False(GzipMemberAttestation.IsAttested(trailer, 600, Crc32.Of(content), content.Length));
    }

    [Fact]
    public void ALongerContentThanTheTrailerClaimsIsNotAttested()
    {
        var content = GzipMember.Content(512);
        var trailer = GzipMember.Trailer(Crc32.Of(content), content.Length);

        Assert.False(GzipMemberAttestation.IsAttested(trailer, 600, Crc32.Of(content), content.Length + 1));
    }

    [Fact]
    public void TheLengthIsComparedModuloTwoToTheThirtyTwo()
    {
        // A content of 2^32 + 5 bytes claims the same trailer value as one of 5 bytes.
        var above = (1L << 32) + 5;
        var trailer = GzipMember.Trailer(0, 5);

        Assert.True(GzipMemberAttestation.IsAttested(trailer, 20, 0, above));
        Assert.False(GzipMemberAttestation.IsAttested(trailer, 20, 0, above + 1));
        Assert.True(GzipMemberAttestation.IsAttested(GzipMember.Trailer(0, 0), 20, 0, 1L << 32));
    }

    [Fact]
    public void ATailShorterThanEightBytesIsNotAttested()
    {
        Assert.False(GzipMemberAttestation.IsAttested([1, 2, 3], 40, 0, 0));
    }

    [Fact]
    public void TheFloorAndTheTrailerSizeAreTheStatedValues()
    {
        Assert.Equal(20, GzipMemberAttestation.MinimumObjectBytes);
        Assert.Equal(8, GzipMemberAttestation.TrailerBytes);
        Assert.Equal(20, GzipMember.Empty().Length);
    }
}
