namespace ReportService.Templates.Tests;

/// <summary>
/// The prepared package cache: a package is parsed once per hash, engine and engine
/// version, at most sixteen are held, and the least recently used one leaves first.
/// </summary>
public sealed class PreparedTemplateCacheTests
{
    [Fact]
    public void APackageIsPreparedOnceForItsKey()
    {
        var cache = new PreparedTemplateCache();
        var preparations = 0;
        PreparedTemplate Prepare()
        {
            preparations++;
            return Template();
        }

        var first = cache.GetOrAdd(new string('a', 64), Prepare);
        var second = cache.GetOrAdd(new string('a', 64), Prepare);

        Assert.Same(first, second);
        Assert.Equal(1, preparations);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TheLeastRecentlyUsedPackageLeavesWhenTheCacheIsFull()
    {
        var cache = new PreparedTemplateCache(capacity: 2);
        var a = new string('a', 64);
        var b = new string('b', 64);
        var c = new string('c', 64);

        cache.GetOrAdd(a, () => Template());
        cache.GetOrAdd(b, () => Template());
        cache.GetOrAdd(a, () => Template());
        cache.GetOrAdd(c, () => Template());

        Assert.Equal(2, cache.Count);
        // b was the least recently used, so it is prepared again rather than returned.
        var preparations = 0;
        cache.GetOrAdd(b, () =>
        {
            preparations++;
            return Template();
        });

        Assert.Equal(1, preparations);
    }

    [Fact]
    public void TwoVersionsOfOnePackageAreTwoEntries()
    {
        var cache = new PreparedTemplateCache();
        var first = cache.GetOrAdd(new string('a', 64), () => Template());
        var second = cache.GetOrAdd(new string('b', 64), () => Template(engineVersion: "7.6.0"));

        Assert.NotSame(first, second);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void AKeyIsRequired()
    {
        var cache = new PreparedTemplateCache();

        Assert.Throws<ArgumentException>(() => cache.GetOrAdd(string.Empty, () => Template()));
        Assert.Throws<ArgumentNullException>(() => cache.GetOrAdd(new string('a', 64), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreparedTemplateCache(capacity: 0));
    }

    private static PreparedTemplate Template(string engineVersion = ScribanTemplateAdapter.PinnedVersion) =>
        new(
            new RenderManifest(
                ScribanTemplateAdapter.EngineName,
                engineVersion,
                new Dictionary<TemplatePart, string>(),
                [],
                new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
                new LocaleDefaults("en", "UTC"),
                GroupKey: null),
            new Dictionary<TemplatePart, PreparedPart>());
}
