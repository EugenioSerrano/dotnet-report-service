using ReportService.TemplatePackages.Keys;

namespace ReportService.TemplatePackages.Tests.Keys;

/// <summary>
/// The key a taken package is stored under: content-addressed, built from
/// encoded segments, and never the path it was read from.
/// </summary>
public sealed class TemplatePackageKeysTests
{
    private const string Sha = "5e88e6f1b5f8d8f0eaa2b2a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b2c4";

    [Fact]
    public void TheKeyIsTheClassTheOwnerTheTemplateTheVersionAndTheHash()
    {
        var key = TemplatePackageKeys.ForVersion("billing-system", "invoice-energy", "1.1.0", Sha);

        Assert.Equal("templates/billing-system/invoice-energy/1.1.0/" + Sha, key.ToString());
    }

    [Fact]
    public void AValueThatIsNotASegmentIsHashedInsideTheKey()
    {
        var key = TemplatePackageKeys.ForVersion("billing-system", "invoice-energy", "1.0.0+build.5", Sha);

        var segments = key.Segments;
        Assert.Equal(5, segments.Count);
        Assert.StartsWith(KeySegmentEncoding.HashedPrefix, segments[3], StringComparison.Ordinal);
        Assert.Equal(KeySegmentEncoding.HashedLength, segments[3].Length);
    }

    [Fact]
    public void TheLongestKeyStaysUnderTheStoragePathLimit()
    {
        var key = TemplatePackageKeys.ForVersion(
            new string('A', 200),
            new string('+', 200),
            new string('_', 200),
            Sha);

        Assert.Equal(TemplatePackageKeys.MaxKeyLength, key.ToString().Length);
        Assert.True(key.ToString().Length < 248);
    }

    [Fact]
    public void ADifferentPackageHashGivesADifferentKey()
    {
        var one = TemplatePackageKeys.ForVersion("billing-system", "invoice-energy", "1.1.0", Sha);
        var other = TemplatePackageKeys.ForVersion("billing-system", "invoice-energy", "1.1.0", new string('a', 64));

        Assert.NotEqual(one, other);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5E88E6F1B5F8D8F0EAA2B2A4B6C8D0E2F4A6B8C0D2E4F6A8B0C2D4E6F8A0B2C4")]
    [InlineData("5e88")]
    [InlineData("z")]
    public void APackageHashThatIsNotSixtyFourLowerCaseHexIsRefused(string hash)
    {
        Assert.False(TemplatePackageKeys.IsSha256(hash));
        Assert.Throws<ArgumentException>(() => TemplatePackageKeys.ForVersion("owner", "template", "1.0.0", hash));
    }

    [Fact]
    public void TheOwnerTemplateAndVersionAreRequired()
    {
        Assert.Throws<ArgumentException>(() => TemplatePackageKeys.ForVersion(string.Empty, "template", "1.0.0", Sha));
        Assert.Throws<ArgumentException>(() => TemplatePackageKeys.ForVersion("owner", string.Empty, "1.0.0", Sha));
        Assert.Throws<ArgumentException>(() => TemplatePackageKeys.ForVersion("owner", "template", string.Empty, Sha));
    }
}
