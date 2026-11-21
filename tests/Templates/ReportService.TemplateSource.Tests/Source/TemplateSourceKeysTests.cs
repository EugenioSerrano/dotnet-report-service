using ReportService.Storage;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplateSource.Source;


namespace ReportService.TemplateSource.Tests.Source;

/// <summary>
/// The source class's key grammar: the layout the publication is read from, the
/// descriptor's last segment being its own hash and never an encoded caller
/// value, the file key of an eight-segment path, the port's budget, and the
/// prefix-versus-key case that a string prefix would confuse.
/// </summary>
public sealed class TemplateSourceKeysTests
{
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";

    [Fact]
    public void ThePrefixesAreTheClassTheTemplateAndTheKind()
    {
        Assert.Equal("template-source/invoice-energy/published", TemplateSourceKeys.PublishedPrefix(Template));
        Assert.Equal("template-source/invoice-energy/published/1.1.0", TemplateSourceKeys.PublishedVersionPrefix(Template, Version));
        Assert.Equal("template-source/invoice-energy/files/1.1.0", TemplateSourceKeys.FilesPrefix(Template, Version));
    }

    [Fact]
    public void AValueThatIsNotKeySafeIsHashedInEveryPrefix()
    {
        const string template = "Invoice Energy";
        const string version = "1.1.0 alpha";

        Assert.Equal($"template-source/{KeySegmentEncoding.Encode(template)}/published", TemplateSourceKeys.PublishedPrefix(template));
        Assert.Equal($"template-source/{KeySegmentEncoding.Encode(template)}/files/{KeySegmentEncoding.Encode(version)}", TemplateSourceKeys.FilesPrefix(template, version));
    }

    [Fact]
    public void TheDescriptorKeyEndsInItsOwnHashAndNotInAnEncodedValue()
    {
        var sha = new string('a', 64);

        var key = TemplateSourceKeys.Descriptor(Template, Version, sha);

        Assert.Equal(["template-source", Template, "published", Version, sha + ".json"], key.Segments);
        Assert.Equal(5, key.Segments.Count);
        Assert.False(key.Segments[^1].StartsWith("h-", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDescriptorRefusesAValueThatIsNotASha256()
    {
        Assert.Throws<ArgumentException>(() => TemplateSourceKeys.Descriptor(Template, Version, "not-a-hash"));
        Assert.Throws<ArgumentException>(() => TemplateSourceKeys.Descriptor(Template, Version, new string('A', 64)));
    }

    [Fact]
    public void AFileKeyIsExactlyTheClassTheTemplateTheKindTheVersionAndThePath()
    {
        var key = TemplateSourceKeys.File(Template, Version, "html/row.html.sbn");

        Assert.Equal(["template-source", Template, "files", Version, "html", "row.html.sbn"], key.Segments);
    }

    [Fact]
    public void AFileKeyRefusesAPathNoPackageMayHold()
    {
        Assert.Throws<ArgumentException>(() => TemplateSourceKeys.File(Template, Version, "../template.yaml"));
        Assert.Throws<ArgumentException>(() => TemplateSourceKeys.File(Template, Version, "a/b/c/d/e/f/g/h/i"));
        Assert.Throws<ArgumentException>(() => TemplateSourceKeys.File(Template, Version, "dir//file"));
    }

    [Fact]
    public void AnEightSegmentPathHoldsThePortsBudget()
    {
        // Every segment of this path has an upper-case letter, so each is encoded
        // to the hashed form: the worst case the class's budget is stated for.
        var path = string.Join('/', Enumerable.Repeat("Segment", TemplatePackages.Packages.PackagePath.MaxSegments));

        var key = TemplateSourceKeys.File(Template, "1.1.0+build.1", path);

        Assert.True(key.Segments.Count <= ObjectKey.MaxSegments, $"{key.Segments.Count} segments");
        Assert.True(key.ToString().Length <= ObjectKey.MaxLength, $"{key.ToString().Length} characters");
        Assert.Equal(12, key.Segments.Count);
    }

    [Fact]
    public void TheLongestEncodedSegmentsStillFit()
    {
        var template = new string('a', 33);
        var version = "1.0.0+" + new string('b', 40);
        var segment = new string('A', 29);
        var path = string.Join('/', Enumerable.Repeat(segment, TemplatePackages.Packages.PackagePath.MaxSegments));

        var key = TemplateSourceKeys.File(template, version, path);

        Assert.True(key.Segments.Count <= ObjectKey.MaxSegments);
        Assert.True(key.ToString().Length <= ObjectKey.MaxLength, $"{key.ToString().Length} characters");
        Assert.All(key.Segments, segmentText => Assert.InRange(segmentText.Length, 1, ObjectKey.MaxSegmentLength));
    }

    [Fact]
    public void AVersionsPrefixIsNotTheKeyOfItsPreReleaseSibling()
    {
        var stable = TemplateSourceKeys.Descriptor(Template, "1.1.0", new string('a', 64));
        var preRelease = TemplateSourceKeys.Descriptor(Template, "1.1.0-rc.1", new string('b', 64));

        Assert.False(TemplateSourceKeys.TryReadDescriptorName(preRelease, Template, "1.1.0", out _, out _));
        Assert.True(TemplateSourceKeys.TryReadDescriptorName(stable, Template, "1.1.0", out var sha, out _));
        Assert.Equal(new string('a', 64), sha);
    }

    [Fact]
    public void AnEntryOutsideTheLayoutIsNotARecordName()
    {
        var wrongSuffix = ObjectKey.Create($"template-source/{Template}/published/{Version}/{new string('a', 64)}.txt");
        var shortHash = ObjectKey.Create($"template-source/{Template}/published/{Version}/{new string('a', 63)}.json");
        var wrongDepth = ObjectKey.Create($"template-source/{Template}/published/{Version}/nested/{new string('a', 64)}.json");

        Assert.False(TemplateSourceKeys.TryReadDescriptorName(wrongSuffix, Template, Version, out _, out _));
        Assert.False(TemplateSourceKeys.TryReadDescriptorName(shortHash, Template, Version, out _, out _));
        Assert.False(TemplateSourceKeys.TryReadDescriptorName(wrongDepth, Template, Version, out _, out _));
    }

    [Fact]
    public void TheEnumerationAcceptsARecordUnderAVersionSegmentAndReportsItsText()
    {
        var key = TemplateSourceKeys.Descriptor(Template, "1.1.0-rc.1", new string('a', 64));

        Assert.True(TemplateSourceKeys.TryReadPublishedRecord(key, Template, out var versionSegment, out var sha, out _));
        Assert.Equal("1.1.0-rc.1", versionSegment);
        Assert.Equal(new string('a', 64), sha);
    }

    [Fact]
    public void ADeviceNameOrALongTemplateIsEncodedRatherThanWrittenRaw()
    {
        Assert.StartsWith("h-", TemplateSourceKeys.PublishedPrefix("con").Split('/')[1], StringComparison.Ordinal);
        Assert.StartsWith("h-", TemplateSourceKeys.PublishedPrefix(new string('a', 33)).Split('/')[1], StringComparison.Ordinal);
    }
}
