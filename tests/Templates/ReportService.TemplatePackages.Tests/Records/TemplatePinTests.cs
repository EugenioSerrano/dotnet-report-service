using System.Reflection;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;

namespace ReportService.TemplatePackages.Tests.Provenance;

/// <summary>
/// The pin a job's reference carries once its version was resolved: it records
/// one of the two source shapes a taken version can hold - the commit a version
/// taken before the object-store source was read at, or the publication
/// descriptor's hash and the verified file list of a version taken from it - and
/// never a mixture or neither. The pin has no member for the owner, the shared
/// flag, the descriptor's key or the package's storage key: those are the taken
/// version's, and the answer reads them there.
/// </summary>
public sealed class TemplatePinTests
{
    private static readonly DateTimeOffset ResolvedAt = new(2026, 11, 1, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AGitTakenRecordProducesAGitPin()
    {
        var record = TemplateVersionRecords.Sample();

        var pin = TemplatePin.From(record, ResolvedAt);

        Assert.Equal(record.TemplateId, pin.TemplateId);
        Assert.Equal(record.Version, pin.ResolvedVersion);
        Assert.Equal(record.CommitSha, pin.CommitSha);
        Assert.Null(pin.SourceDescriptorSha256);
        Assert.Null(pin.SourceObjects);
        Assert.Equal(record.PackageSha256, pin.PackageSha256);
        Assert.Equal(record.Engine, pin.Engine);
        Assert.Equal(record.EngineVersion, pin.EngineVersion);
        Assert.Equal(record.SchemaRef, pin.SchemaRef);
        Assert.Equal(ResolvedAt, pin.ResolvedAt);
    }

    [Fact]
    public void AStorageTakenRecordProducesAStoragePin()
    {
        var record = TemplateVersionRecords.StorageSample();

        var pin = TemplatePin.From(record, ResolvedAt);

        Assert.Equal(record.TemplateId, pin.TemplateId);
        Assert.Equal(record.Version, pin.ResolvedVersion);
        Assert.Null(pin.CommitSha);
        Assert.Equal(record.SourceDescriptorSha256, pin.SourceDescriptorSha256);
        Assert.Equal(record.SourceObjects, pin.SourceObjects);
        Assert.NotSame(record.SourceObjects, pin.SourceObjects);
        Assert.Equal(record.PackageSha256, pin.PackageSha256);
        Assert.Equal(record.Engine, pin.Engine);
        Assert.Equal(record.EngineVersion, pin.EngineVersion);
        Assert.Equal(record.SchemaRef, pin.SchemaRef);
        Assert.Equal(ResolvedAt, pin.ResolvedAt);
    }

    [Fact]
    public void ARecordWithNoSourceProvenanceIsAFaultAndNeverAGuess()
    {
        var record = TemplateVersionRecords.Sample() with { CommitSha = null, SourceTreeSha = null };

        var error = Assert.Throws<InvalidOperationException>(() => TemplatePin.From(record, ResolvedAt));

        Assert.Contains("no source provenance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordWithBothSourceShapesIsAFault()
    {
        var record = TemplateVersionRecords.Sample() with
        {
            SourceDescriptorSha256 = TemplateVersionRecords.DescriptorSha256,
            SourceDescriptorKey = TemplateVersionRecords.DescriptorKeyFor("invoice-energy", "1.1.0"),
            SourceObjects = TemplateVersionRecords.Objects,
        };

        var error = Assert.Throws<InvalidOperationException>(() => TemplatePin.From(record, ResolvedAt));

        Assert.Contains("two source provenances", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePinHasNoMemberForTheOwnerTheSharedFlagTheDescriptorKeyOrTheObjectKey()
    {
        var members = typeof(TemplatePin)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "CommitSha", "Engine", "EngineVersion", "PackageSha256", "ResolvedAt",
                "ResolvedVersion", "SchemaRef", "SourceDescriptorSha256", "SourceObjects", "TemplateId",
            ],
            members);
        Assert.DoesNotContain("Owner", members);
        Assert.DoesNotContain("OwnerConsumerId", members);
        Assert.DoesNotContain("Shared", members);
        Assert.DoesNotContain("SourceDescriptorKey", members);
        Assert.DoesNotContain("ObjectKey", members);
    }
}
