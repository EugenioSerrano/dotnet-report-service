using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;

namespace ReportService.TemplatePackages.Tests.Visibility;

/// <summary>
/// The one ownership predicate: a version is usable when it is shared or when it
/// belongs to the consumer asking, and by nothing else — the comparison is
/// ordinal, so an identifier that differs only by case is another consumer.
/// </summary>
public sealed class TemplateVersionVisibilityTests
{
    [Fact]
    public void ASharedVersionIsUsableByEveryConsumer()
    {
        var record = Record(owner: "billing-system", shared: true);

        Assert.True(TemplateVersionVisibility.MayUse(record, "reporting-system"));
        Assert.True(TemplateVersionVisibility.MayUse(record, "billing-system"));
    }

    [Fact]
    public void TheOwnerMayUseItsOwnVersion()
    {
        var record = Record(owner: "billing-system", shared: false);

        Assert.True(TemplateVersionVisibility.MayUse(record, "billing-system"));
    }

    [Fact]
    public void AnotherConsumerMayNotUseANonSharedVersion()
    {
        var record = Record(owner: "billing-system", shared: false);

        Assert.False(TemplateVersionVisibility.MayUse(record, "reporting-system"));
    }

    [Fact]
    public void TheComparisonIsOrdinalSoACaseVariantIsAnotherConsumer()
    {
        var record = Record(owner: "billing-system", shared: false);

        Assert.False(TemplateVersionVisibility.MayUse(record, "Billing-System"));
    }

    [Fact]
    public void AConsumerThatNamesNobodyIsNeverTheOwner()
    {
        var record = Record(owner: "billing-system", shared: false);

        Assert.False(TemplateVersionVisibility.MayUse(record, string.Empty));
        Assert.False(TemplateVersionVisibility.MayUse(record, " "));
        Assert.False(TemplateVersionVisibility.MayUse(record, "billing-system "));
    }

    private static TemplateVersionRecord Record(string owner, bool shared) =>
        new(
            TemplateId: "invoice-energy",
            Version: "1.1.0",
            OwnerConsumerId: owner,
            Shared: shared,
            ManifestVersion: "1.0",
            Engine: TemplateEngine.Scriban,
            EngineVersion: "7.5.1",
            SchemaRef: "schema/data.schema.json",
            CommitSha: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
            SourceTreeSha: "b2c3d4e5f60718293a4b5c6d7e8f90123456789a",
            PackageSha256: new string('a', 64),
            PackageSize: 4_096,
            FileCount: 4,
            ObjectKey: TemplatePackageKeys.ForVersion(owner, "invoice-energy", "1.1.0", new string('a', 64)),
            Manifest: new TemplateManifest(),
            ManifestJson: "{}",
            TakenAt: new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
}
