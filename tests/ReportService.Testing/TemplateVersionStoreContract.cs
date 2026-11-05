using ReportService.TemplatePackages.Records;
using ReportService.Testing.Manifests;

namespace ReportService.Testing;

/// <summary>
/// The contract every template-version store is held to: the in-memory double of
/// the unit level here, and the PostgreSQL store in the integration suite. A
/// version that was never written is not found; what was written is found with the
/// values it was written with; and a second insert of one identity answers the
/// record that is already there instead of replacing it, which is the rule the
/// whole immutability of a taken version rests on. The list read answers one
/// template's versions and nothing else, in the order the table's byte collation
/// gives them.
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
        Assert.True(
            ManifestDocuments.AreTheSameDocument(written.ManifestJson, found.ManifestJson),
            $"the manifest of the version found is not the document that was written.{Environment.NewLine}"
            + $"written: {written.ManifestJson}{Environment.NewLine}"
            + $"found: {found.ManifestJson}");
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

    [Fact]
    public async Task ATemplateWithNoTakenVersionAnswersNoRecords()
    {
        var records = await Store.ListByTemplateAsync("invoice-energy", TestContext.Current.CancellationToken);

        Assert.Empty(records);
    }

    [Fact]
    public async Task TheTakenVersionsOfOneTemplateComeBackAndNothingElse()
    {
        var one = TemplateVersionRecords.Sample(version: "1.0.0");
        var two = TemplateVersionRecords.Sample(version: "1.1.0");
        var another = TemplateVersionRecords.Sample(templateId: "invoice-other", version: "1.0.0");
        await Store.TryInsertAsync(one, TestContext.Current.CancellationToken);
        await Store.TryInsertAsync(two, TestContext.Current.CancellationToken);
        await Store.TryInsertAsync(another, TestContext.Current.CancellationToken);

        var records = await Store.ListByTemplateAsync(one.TemplateId, TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);
        Assert.Equal(["1.0.0", "1.1.0"], records.Select(record => record.Version));
        Assert.All(records, record => Assert.Equal(one.TemplateId, record.TemplateId));
        Assert.Equal(one.PackageSha256, records[0].PackageSha256);
        Assert.Equal(one.Engine, records[0].Engine);
        Assert.True(
            ManifestDocuments.AreTheSameDocument(one.ManifestJson, records[0].ManifestJson),
            $"the manifest of the version listed is not the document that was written.{Environment.NewLine}"
            + $"written: {one.ManifestJson}{Environment.NewLine}"
            + $"listed: {records[0].ManifestJson}");
    }

    [Fact]
    public async Task SeveralVersionsComeBackInByteOrder()
    {
        foreach (var version in new[] { "1.2.0", "1.10.0", "1.1.0", "1.0.0" })
        {
            await Store.TryInsertAsync(
                TemplateVersionRecords.Sample(version: version),
                TestContext.Current.CancellationToken);
        }

        var records = await Store.ListByTemplateAsync("invoice-energy", TestContext.Current.CancellationToken);

        Assert.Equal(["1.0.0", "1.1.0", "1.10.0", "1.2.0"], records.Select(record => record.Version));
    }
}
