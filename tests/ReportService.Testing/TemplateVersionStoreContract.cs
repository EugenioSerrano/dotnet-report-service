using ReportService.TemplatePackages.Records;

namespace ReportService.Testing;

/// <summary>
/// The contract every template-version store is held to: the in-memory double of
/// the unit level here, and the PostgreSQL store in the integration suite. A
/// version that was never written is not found; what was written is found with the
/// values it was written with; and a second insert of one identity answers the
/// record that is already there instead of replacing it, which is the rule the
/// whole immutability of a taken version rests on.
/// </summary>
public abstract class TemplateVersionStoreContract
{
    /// <summary>The store the cases of this contract run against.</summary>
    protected abstract ITemplateVersionStore Store { get; }

    [Fact]
    public async Task AVersionThatWasNeverWrittenIsNotFound()
    {
        var found = await Store.FindAsync("invoice-energy", "9.9.9", TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    [Fact]
    public async Task WhatWasWrittenIsFoundWithTheValuesItWasWrittenWith()
    {
        var written = TemplateVersionRecords.Sample();
        var insert = await Store.TryInsertAsync(written, TestContext.Current.CancellationToken);

        Assert.True(insert.WasInserted);
        var found = await Store.FindAsync(written.TemplateId, written.Version, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal(written.TemplateId, found.TemplateId);
        Assert.Equal(written.Version, found.Version);
        Assert.Equal(written.OwnerConsumerId, found.OwnerConsumerId);
        Assert.Equal(written.Shared, found.Shared);
        Assert.Equal(written.Engine, found.Engine);
        Assert.Equal(written.EngineVersion, found.EngineVersion);
        Assert.Equal(written.SchemaRef, found.SchemaRef);
        Assert.Equal(written.CommitSha, found.CommitSha);
        Assert.Equal(written.SourceTreeSha, found.SourceTreeSha);
        Assert.Equal(written.PackageSha256, found.PackageSha256);
        Assert.Equal(written.PackageSize, found.PackageSize);
        Assert.Equal(written.FileCount, found.FileCount);
        Assert.Equal(written.ObjectKey, found.ObjectKey);
        Assert.Equal(written.ManifestJson, found.ManifestJson);
        Assert.Equal(written.TakenAt, found.TakenAt);
    }

    [Fact]
    public async Task ASecondInsertOfTheSameIdentityAnswersTheRecordThatIsAlreadyThere()
    {
        var first = TemplateVersionRecords.Sample();
        var second = TemplateVersionRecords.Sample(
            owner: "another-system",
            packageSha256: new string('b', 64),
            takenAt: first.TakenAt.AddHours(1));

        var inserted = await Store.TryInsertAsync(first, TestContext.Current.CancellationToken);
        var refused = await Store.TryInsertAsync(second, TestContext.Current.CancellationToken);

        Assert.True(inserted.WasInserted);
        Assert.False(refused.WasInserted);
        Assert.Equal(first.OwnerConsumerId, refused.Record.OwnerConsumerId);
        Assert.Equal(first.PackageSha256, refused.Record.PackageSha256);
        Assert.Equal(first.TakenAt, refused.Record.TakenAt);

        // One version, one record: the identity is the template and the version, never the owner.
        var found = await Store.FindAsync(first.TemplateId, first.Version, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal(first.PackageSha256, found.PackageSha256);
    }
}
