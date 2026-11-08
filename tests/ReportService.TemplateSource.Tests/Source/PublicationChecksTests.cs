using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Source;
using SourcePackageFile = ReportService.TemplateSource.Source.SourceFile;

namespace ReportService.TemplateSource.Tests.Source;

/// <summary>
/// The publication record's own checks, one case per rule and a passing control
/// just inside each bound. Every reason names the offending file, field or cap
/// and never a storage key, a provider message or a credential.
/// </summary>
public sealed class PublicationChecksTests
{
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";
    private static readonly string Hash = new('a', 64);

    private static readonly TemplateSourceOptions Caps = new()
    {
        MaxFiles = 3,
        MaxFileBytes = 10,
        MaxPackageBytes = 30,
    };

    [Fact]
    public void ARecordAtEveryBoundPasses()
    {
        var descriptor = Descriptor(
        [
            new SourcePackageFile("schema.json", 10, Hash),
            new SourcePackageFile("template.yaml", 10, Hash),
            new SourcePackageFile("row.html.sbn", 10, Hash),
        ]);

        Assert.Null(PublicationChecks.Check(descriptor, Template, Version, Caps));
    }

    [Fact]
    public void AContractVersionThisBuildDoesNotReadIsUnsupported()
    {
        var refusal = Check(Descriptor(files: [new SourcePackageFile("template.yaml", 1, Hash)], descriptorVersion: "2.0"));

        Assert.NotNull(refusal);
        Assert.Equal(SourceReadRefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("2.0", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("does not read", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherTemplateIsRefusedNamingBoth()
    {
        var refusal = Check(Descriptor(files: [new SourcePackageFile("template.yaml", 1, Hash)], templateId: "invoice-other"));

        Assert.NotNull(refusal);
        Assert.Equal(SourceReadRefusalKind.Invalid, refusal.Kind);
        Assert.Contains("invoice-other", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains(Template, refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherVersionIsRefusedNamingBoth()
    {
        var refusal = Check(Descriptor(files: [new SourcePackageFile("template.yaml", 1, Hash)], version: "1.2.0"));

        Assert.NotNull(refusal);
        Assert.Equal(SourceReadRefusalKind.Invalid, refusal.Kind);
        Assert.Contains(Version, refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("1.2.0", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFileListIsRefusedNamingTheCountAndTheCap()
    {
        var refusal = Check(Descriptor(files: []));

        Assert.NotNull(refusal);
        Assert.Contains("0", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("3", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreFilesThanTheCapIsRefused()
    {
        var refusal = Check(Descriptor(
        [
            new SourcePackageFile("a.json", 1, Hash),
            new SourcePackageFile("b.json", 1, Hash),
            new SourcePackageFile("c.json", 1, Hash),
            new SourcePackageFile("template.yaml", 1, Hash),
        ]));

        Assert.NotNull(refusal);
        Assert.Contains("4", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("3", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APathThePackageRuleRefusesIsNamed()
    {
        var refusal = Check(Descriptor(files: [new SourcePackageFile("template.yaml", 1, Hash), new SourcePackageFile("../escape.json", 1, Hash)]));

        Assert.NotNull(refusal);
        Assert.Contains("../escape.json", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedPathIsRefusedNamingIt()
    {
        var refusal = Check(Descriptor(
        [
            new SourcePackageFile("template.yaml", 1, Hash),
            new SourcePackageFile("schema.json", 1, Hash),
            new SourcePackageFile("schema.json", 2, Hash),
        ]));

        Assert.NotNull(refusal);
        Assert.Contains("schema.json", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("more than once", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAndAFolderUnderOneNameIsRefused()
    {
        var refusal = Check(Descriptor(
        [
            new SourcePackageFile("template.yaml", 1, Hash),
            new SourcePackageFile("html", 1, Hash),
            new SourcePackageFile("html/row.html.sbn", 1, Hash),
        ]));

        Assert.NotNull(refusal);
        Assert.Contains("html", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("folder", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroByteFileIsRefusedNamingTheFile()
    {
        var refusal = Check(Descriptor(files: [new SourcePackageFile("template.yaml", 0, Hash)]));

        Assert.NotNull(refusal);
        Assert.Contains("template.yaml", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("0", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOverTheCapIsRefusedNamingTheFileAndTheCap()
    {
        var refusal = Check(Descriptor(files: [new SourcePackageFile("template.yaml", 11, Hash)]));

        Assert.NotNull(refusal);
        Assert.Contains("template.yaml", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("10", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AHashThatIsNotSixtyFourLowerCaseHexIsRefusedNamingTheFile()
    {
        var refusal = Check(Descriptor(files: [new SourcePackageFile("template.yaml", 1, new string('A', 64))]));

        Assert.NotNull(refusal);
        Assert.Contains("template.yaml", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("SHA-256", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATotalOverTheCapIsRefusedNamingTheTotalAndTheCap()
    {
        var caps = new TemplateSourceOptions { MaxFiles = 3, MaxFileBytes = 10, MaxPackageBytes = 25 };
        var descriptor = Descriptor(
        [
            new SourcePackageFile("template.yaml", 10, Hash),
            new SourcePackageFile("schema.json", 10, Hash),
            new SourcePackageFile("row.html.sbn", 10, Hash),
        ]);

        var refusal = PublicationChecks.Check(descriptor, Template, Version, caps);

        Assert.NotNull(refusal);
        Assert.Contains("30", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("25", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountThatDisagreesWithTheEntriesIsRefusedNamingBothNumbers()
    {
        var descriptor = Descriptor(files: [new SourcePackageFile("template.yaml", 1, Hash)]) with { FileCount = 2 };

        var refusal = Check(descriptor);

        Assert.NotNull(refusal);
        Assert.Contains("2", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("1", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATotalThatDisagreesWithTheEntriesIsRefusedNamingBothNumbers()
    {
        var descriptor = Descriptor(files: [new SourcePackageFile("template.yaml", 1, Hash)]) with { TotalSize = 9 };

        var refusal = Check(descriptor);

        Assert.NotNull(refusal);
        Assert.Contains("9", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("1", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRootManifestIsRefusedByItsOwnText()
    {
        var refusal = Check(Descriptor(files: [new SourcePackageFile("schema.json", 1, Hash)]));

        Assert.NotNull(refusal);
        Assert.Equal("The publication record does not name template.yaml at the root.", refusal.Reason);
    }

    [Fact]
    public void EveryReasonNamesNoStorageKeyAPathOrACredential()
    {
        var descriptors = new[]
        {
            Descriptor(files: []),
            Descriptor(files: [new SourcePackageFile("template.yaml", 0, Hash)]),
            Descriptor(files: [new SourcePackageFile("../escape.json", 1, Hash)]),
            Descriptor(files: [new SourcePackageFile("template.yaml", 1, new string('A', 64))]),
            Descriptor(files: [new SourcePackageFile("template.yaml", 1, Hash)], version: "9.9.9"),
        };

        foreach (var descriptor in descriptors)
        {
            var refusal = Check(descriptor);
            Assert.NotNull(refusal);
            Assert.DoesNotContain("template-source/", refusal.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("h-", refusal.Reason, StringComparison.Ordinal);
        }
    }

    private static SourceReadRefusal? Check(PublicationDescriptor descriptor) =>
        PublicationChecks.Check(descriptor, Template, Version, Caps);

    private static PublicationDescriptor Descriptor(
        IReadOnlyList<SourcePackageFile> files,
        string descriptorVersion = "1.0",
        string templateId = Template,
        string version = Version) =>
        new(
            descriptorVersion,
            templateId,
            version,
            files,
            files.Count,
            files.Sum(file => file.Size));
}
