using ReportService.TemplateSource.Source;

namespace ReportService.TemplateSource.Tests.Source;

/// <summary>
/// The contract versions this build reads, stated as one fact: the set the start and
/// the health check carry is exactly the one the reader accepts, and a record written
/// to any other version is refused at read time exactly as it is today.
/// </summary>
public sealed class PublicationVersionsTests
{
    [Fact]
    public void TheDeclaredVersionsAreTheOneThisBuildReads()
    {
        Assert.Equal(["1.0"], PublicationChecks.DescriptorVersions);
        Assert.Contains(PublicationChecks.DescriptorVersion, PublicationChecks.DescriptorVersions);
        Assert.Single(PublicationChecks.DescriptorVersions);
    }

    [Fact]
    public void ARecordOfTheDeclaredVersionIsAcceptedByThatRule()
    {
        var refusal = PublicationChecks.Check(Record(PublicationChecks.DescriptorVersion), "invoice-energy", "1.1.0", new TemplateSourceOptions());

        Assert.Null(refusal);
    }

    [Fact]
    public void ARecordOfAnyOtherVersionIsRefusedExactlyAsBefore()
    {
        var record = Record("2.0");

        var refusal = PublicationChecks.Check(record, "invoice-energy", "1.1.0", new TemplateSourceOptions());

        Assert.NotNull(refusal);
        Assert.Equal(SourceReadRefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("'2.0'", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("does not read", refusal.Reason, StringComparison.Ordinal);
    }

    private static PublicationDescriptor Record(string descriptorVersion)
    {
        var file = new SourceFile("template.yaml", 4, new string('a', 64));
        return new PublicationDescriptor(descriptorVersion, "invoice-energy", "1.1.0", [file], 1, 4);
    }
}
