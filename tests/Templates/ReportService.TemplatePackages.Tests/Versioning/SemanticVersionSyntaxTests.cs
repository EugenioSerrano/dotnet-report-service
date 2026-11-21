using ReportService.TemplatePackages.Versioning;

namespace ReportService.TemplatePackages.Tests.Versioning;

/// <summary>
/// The semantic version syntax, checked with the specification's own examples.
/// Precedence is not decided here and is not asserted.
/// </summary>
public sealed class SemanticVersionSyntaxTests
{
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
    public void AValidVersionIsAccepted(string version)
    {
        Assert.True(SemanticVersionSyntax.IsValid(version));
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
    public void AnInvalidVersionIsRefused(string version)
    {
        Assert.False(SemanticVersionSyntax.IsValid(version));
    }

    [Fact]
    public void AVStringLongerThanTheLimitIsRefused()
    {
        var version = "1.0.0-" + new string('a', SemanticVersionSyntax.MaxLength);

        Assert.False(SemanticVersionSyntax.IsValid(version));
        Assert.True(SemanticVersionSyntax.IsValid("1.0.0-" + new string('a', 20)));
    }

    [Fact]
    public void NoVersionAtAllIsRefused()
    {
        Assert.False(SemanticVersionSyntax.IsValid(null));
    }
}
