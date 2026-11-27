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

    /// <summary>A store over the same upload records, judging commits against <paramref name="capacity"/>.</summary>
    /// <param name="capacity">The configured document capacity.</param>
    protected override IJobStore CreateStoreAtCapacity(DocumentCapacity capacity) => new InMemoryJobStore(_uploads, capacity);

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

    /// <summary>
    /// Arranges an additional output of the job in the asked state (SPEC-261125-1224 Phase D.6):
    /// a running output is seeded; a succeeded one is seeded and published; a failed one is seeded
    /// and failed; a cancelled one is seeded directly through the double's cancellation seam.
    /// </summary>
    protected override async Task<bool> TrySeedOutputAsync(
        string reportJobId,
        string outputId,
        OutputFormat format,
        OutputStatus status,
        CancellationToken cancellationToken)
    {
        var store = (InMemoryJobStore)Store;
        switch (status)
        {
            case OutputStatus.Running:
                store.SeedOutput(reportJobId, outputId, format);
                return true;

            case OutputStatus.Succeeded:
                store.SeedOutput(reportJobId, outputId, format);
                var published = await store.PublishAsync(
                    ArtifactOf(reportJobId, outputId, "artifact-" + outputId[^32..], format), cancellationToken);
                return published.Outcome == PublishOutcome.Published;

            case OutputStatus.Failed:
                store.SeedOutput(reportJobId, outputId, format);
                var failed = await store.FailOutputAsync(outputId, "UNSUPPORTED_CAPABILITY", null, Now, cancellationToken);
                return failed == OutputFailureOutcome.Applied;

            case OutputStatus.Cancelled:
                // D.6 seam added for cancelled outputs: the double records a terminal cancelled output.
                store.SeedCancelledOutput(reportJobId, outputId, format);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Cancels an existing output of the job in place through the double's cancellation seam (D.6).</summary>
    protected override Task<bool> TryCancelExistingOutputAsync(string outputId, CancellationToken cancellationToken)
    {
        ((InMemoryJobStore)Store).CancelOutput(outputId);
        return Task.FromResult(true);
    }

    /// <summary>Puts the job into the non-terminal Cancelling state through the double's cancellation seam (D.6).</summary>
    protected override Task<bool> TrySeedCancellingJobAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ((InMemoryJobStore)Store).SetJobCancelling(reportJobId);
        return Task.FromResult(true);
    }

    /// <summary>Removes every output of a job through the double's own seam, the shape a job requesting none has.</summary>
    protected override Task<bool> TryRemoveOutputsAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ((InMemoryJobStore)Store).RemoveOutputs(reportJobId);
        return Task.FromResult(true);
    }
}
