using ReportService.TemplatePackages.Versioning;

namespace ReportService.TemplatePackages.Tests.Versioning;

/// <summary>
/// The version precedence, checked with the specification's own example chain
/// and with the cases the resolution depends on: a numeric part is compared
/// numerically, a pre-release is lower than its release, and build metadata
/// never orders anything. Which of two versions of equal precedence a by-name
/// reference uses is not decided here.
/// </summary>
public sealed class SemanticVersionTests
{
    private static SemanticVersion Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version), $"'{text}' should parse");
        return version;
    }

    [Fact]
    public void TheSpecificationsPrecedenceChainHolds()
    {
        string[] chain =
        [
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        ];

        for (var index = 1; index < chain.Length; index++)
        {
            var lower = Parse(chain[index - 1]);
            var higher = Parse(chain[index]);
            Assert.True(lower.CompareTo(higher) < 0, $"{lower.Text} should be lower than {higher.Text}");
            Assert.True(higher.CompareTo(lower) > 0, $"{higher.Text} should be higher than {lower.Text}");
        }
    }

    [Fact]
    public void AVersionPartIsComparedNumericallyNotAsText()
    {
        Assert.True(Parse("1.10.0").CompareTo(Parse("1.2.0")) > 0);
        Assert.True(Parse("2.0.0").CompareTo(Parse("10.0.0")) < 0);
        Assert.True(Parse("1.2.10").CompareTo(Parse("1.2.9")) > 0);
    }

    [Fact]
    public void BuildMetadataNeverDecidesPrecedence()
    {
        var withMetadata = Parse("1.1.0+build.7");
        var otherMetadata = Parse("1.1.0+build.9");
        var plain = Parse("1.1.0");

        Assert.Equal(0, withMetadata.CompareTo(otherMetadata));
        Assert.Equal(0, withMetadata.CompareTo(plain));
        Assert.Equal(plain, withMetadata);
        Assert.Equal(withMetadata.GetHashCode(), otherMetadata.GetHashCode());
        Assert.NotEqual(withMetadata.Text, otherMetadata.Text);
    }

    [Fact]
    public void APreReleaseIsLowerThanTheReleaseWithTheSameParts()
    {
        Assert.True(Parse("1.0.0-rc.1").CompareTo(Parse("1.0.0")) < 0);
        Assert.True(Parse("1.0.0").CompareTo(Parse("1.0.0-rc.1")) > 0);
    }

    [Fact]
    public void ANumericIdentifierIsLowerThanAnAlphanumericOne()
    {
        Assert.True(Parse("1.0.0-2").CompareTo(Parse("1.0.0-alpha")) < 0);
        Assert.True(Parse("1.0.0-alpha").CompareTo(Parse("1.0.0-2")) > 0);
    }

    [Fact]
    public void APreReleaseWithMoreIdentifiersIsHigherWhenTheShorterIsAPrefix()
    {
        Assert.True(Parse("1.0.0-alpha").CompareTo(Parse("1.0.0-alpha.1")) < 0);
        Assert.True(Parse("1.0.0-alpha.1").CompareTo(Parse("1.0.0-alpha")) > 0);
    }

    [Fact]
    public void ThePartsOfAVersionAreReadAsWritten()
    {
        var version = Parse("10.20.30-beta.1+build.5");

        Assert.Equal(10UL, version.Major);
        Assert.Equal(20UL, version.Minor);
        Assert.Equal(30UL, version.Patch);
        Assert.Equal(["beta", "1"], version.PreRelease);
        Assert.Equal(["build", "5"], version.Build);
        Assert.Equal("10.20.30-beta.1+build.5", version.Text);
    }

    [Fact]
    public void AReleaseHasNoPreReleaseIdentifiers()
    {
        var version = Parse("1.1.0+build.2");

        Assert.Empty(version.PreRelease);
        Assert.Equal(["build", "2"], version.Build);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.0.0")]
    [InlineData("1.2.3")]
    [InlineData("10.20.30")]
    [InlineData("1.1.0")]
    [InlineData("1.0.0-0.3.7")]
    [InlineData("1.0.0-x.7.z.92")]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0-alpha.1")]
    [InlineData("1.0.0-x-y-z.--")]
    [InlineData("1.0.0+build.5")]
    [InlineData("1.0.0-beta+exp.sha.5114f85")]
    [InlineData("2.0.0-beta.1")]
    public void EveryVersionTheSyntaxAcceptsIsParsed(string version)
    {
        Assert.True(SemanticVersion.TryParse(version, out var parsed));
        Assert.Equal(version, parsed.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.01.0")]
    [InlineData("1.0.01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-alpha..1")]
    [InlineData("1.0.0-alpha+")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0 ")]
    [InlineData(" 1.0.0")]
    [InlineData("1.0.0\n")]
    [InlineData("ł.0.0")]
    public void EveryVersionTheSyntaxRefusesIsRefused(string version)
    {
        Assert.False(SemanticVersion.TryParse(version, out _));
    }

    [Fact]
    public void NoVersionAtAllIsRefused()
    {
        Assert.False(SemanticVersion.TryParse(null, out _));
    }

    [Fact]
    public void AVeryLongVersionIsParsed()
    {
        var text = "1.0.0-" + new string('a', SemanticVersionSyntax.MaxLength - "1.0.0-".Length);

        Assert.True(SemanticVersion.TryParse(text, out var parsed));
        Assert.Equal(text, parsed.Text);
    }
}
