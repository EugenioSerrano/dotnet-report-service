using ReportService.TemplatePackages.Packages;

namespace ReportService.TemplatePackages.Tests.Packages;

/// <summary>The rule every file of a package must satisfy before it is stored or extracted.</summary>
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
    public void APathLongerThanTheLimitIsRefused()
    {
        var path = "assets/" + new string('a', 240);

        Assert.False(PackagePath.IsValid(path));
    }

    [Fact]
    public void ASingleSegmentLongerThanTheLimitIsRefused()
    {
        Assert.False(PackagePath.IsValid(new string('a', PackagePath.MaxSegmentLength + 1)));
    }

    [Fact]
    public void NoPathAtAllIsRefused()
    {
        Assert.False(PackagePath.IsValid(null));
    }
}
