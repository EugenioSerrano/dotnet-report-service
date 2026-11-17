using ReportService.Jobs;
using ReportService.Testing;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.Jobs.Tests;

/// <summary>The job store contract, run against the in-memory store.</summary>
public sealed class InMemoryJobStoreContractTests : JobStoreContract
{
    private readonly InMemoryUploadStore _uploads = new();

    /// <summary>The taken versions a pinned case records, in memory like the store itself.</summary>
    protected override ITemplateVersionStore? Versions { get; } = new InMemoryTemplateVersionStore();

    /// <summary>The two stores this class's cases run against, both in memory.</summary>
    protected override Task<(IJobStore Jobs, IUploadStore Uploads)> CreateStoresAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(IJobStore, IUploadStore)>((new InMemoryJobStore(_uploads), _uploads));

    /// <summary>
    /// Publishes an artifact of an output beyond the one the acceptance creates: the double
    /// records an output of any format, so the fixed order of the formats is exercised here.
    /// </summary>
    /// <param name="artifact">The artifact to publish, naming the output it belongs to.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected override async Task<bool> TryPublishArtifactOfAnotherOutputAsync(
        ArtifactRecord artifact,
        CancellationToken cancellationToken)
    {
        var store = (InMemoryJobStore)Store;
        store.SeedOutput(artifact.ReportJobId, artifact.OutputId, artifact.Format);
        return (await store.PublishAsync(artifact, cancellationToken)).Outcome == PublishOutcome.Published;
    }
}
