using ReportService.TemplatePackages.Packages;

namespace ReportService.TemplatePackages.Tests.Packages;

/// <summary>
/// The rule every file of a package must satisfy before it is stored or
/// extracted, held at its own boundaries: the longest segment and the longest
/// path the archive format can hold are accepted and one character past each is
/// refused, so no path the rule accepts can fail to be written.
/// </summary>
public sealed class PackagePathTests
{
    [Theory]
    [InlineData("template.yaml")]
    [InlineData("schema.json")]
    [InlineData("sample.json")]
    [InlineData("html/row.html.sbn")]
    [InlineData("html/document-start.html.sbn")]
    [InlineData("assets/fonts/DejaVuSans.ttf")]
    [InlineData("a/b/c/d/e/f/g/h")]
    public void APlainRelativePathIsAccepted(string path)
    {
        Assert.True(PackagePath.IsValid(path));
        Assert.True(PackagePath.IsValid(path, out var reason));
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute/path.txt")]
    [InlineData("../outside.txt")]
    [InlineData("html/../outside.txt")]
    [InlineData("html/./row.html")]
    [InlineData("a\\b.txt")]
    [InlineData("has space.txt")]
    [InlineData("a/b/c/d/e/f/g/h/i")]
    [InlineData("ünïcode.txt")]
    public void AnUnsafeOrOverlongPathIsRefused(string path)
    {
        Assert.False(PackagePath.IsValid(path));
        Assert.False(PackagePath.IsValid(path, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void TheCapsAreTheArchiveFormatsOwnLengths()
    {
        Assert.Equal(100, PackagePath.MaxSegmentLength);
        Assert.Equal(240, PackagePath.MaxLength);
        Assert.Equal(8, PackagePath.MaxSegments);
    }

    [Fact]
    public void ASegmentOfExactlyTheLimitIsAcceptedAndOneCharacterMoreIsRefused()
    {
        var atLimit = new string('a', PackagePath.MaxSegmentLength);

        Assert.True(PackagePath.IsValid(atLimit));
        Assert.False(PackagePath.IsValid(atLimit + "a"));
        Assert.False(PackagePath.IsValid("html/" + atLimit + "a"));
    }

    [Fact]
    public void APathOfExactlyTheLimitIsAcceptedAndOneCharacterMoreIsRefused()
    {
        // Three segments of at most the segment limit, adding up to exactly the path limit.
        var atLimit = new string('a', PackagePath.MaxSegmentLength)
            + "/"
            + new string('b', PackagePath.MaxSegmentLength)
            + "/"
            + new string('c', PackagePath.MaxLength - (2 * PackagePath.MaxSegmentLength) - 2);

        Assert.Equal(PackagePath.MaxLength, atLimit.Length);
        Assert.True(PackagePath.IsValid(atLimit));
        Assert.False(PackagePath.IsValid(atLimit + "c"));
        Assert.False(PackagePath.IsValid("assets/" + new string('d', PackagePath.MaxLength)));
    }

    [Fact]
    public void TheSegmentsOfAPathAreCountedAtTheirLimit()
    {
        var eight = string.Join('/', Enumerable.Repeat("a", PackagePath.MaxSegments));
        var nine = string.Join('/', Enumerable.Repeat("a", PackagePath.MaxSegments + 1));

        Assert.True(PackagePath.IsValid(eight));
        Assert.False(PackagePath.IsValid(nine));
    }

    [Fact]
    public void NoPathAtAllIsRefused()
    {
        Assert.False(PackagePath.IsValid(null));
    }
}
