extern alias orchestration;

using ReportService.Storage;
using ReportService.Testing.VerificationLoss;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Common;
using PdfOutputActivities = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivities;
using PdfOutputStepContracts = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputStepContracts;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The wrong-file boundary on the real provider: the block's PDF passes every check and the bytes
/// the store holds are not the bytes that passed them. The render worker is hosted in this process
/// on its real registrations over the real filesystem provider, wrapped by a decorator that writes
/// the real object and answers a size and a SHA-256 that are not the file's, so the refusal is
/// decided where it is in the product: on the comparison of the write with the verified file.
/// <para>
/// The refusal leaves the written object behind as an orphan - referenced by no part commit and
/// no artifact and never deleted - and the output ends failed with the delivered
/// <c>EXECUTION_FAILED</c>; the failure-code-to-output mapping for that code is the delivered
/// <c>ReportPdfOutputWorkflowTests.ADomainFailureKeepsTheCodeItCarries</c>, cited rather than
/// re-implemented. The SDK-built image stays covered by the job-level class of the corpus'.
/// </para>
/// </summary>
[Trait("Requires", "printing")]
public sealed class WrongFileRefusalIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private const string RefusalCode = "EXECUTION_FAILED";

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public WrongFileRefusalIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A block whose stored bytes are not the verified bytes is refused, commits nothing and
    /// leaves its object as an unreferenced orphan under the output's own prefix.
    /// </summary>
    [Fact]
    public async Task AStoredBlockThatIsNotThePrintedFileIsRefusedAndLeavesAnOrphan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            extraConsumers: [VerificationLossCorpus.Consumer],
            decoratePdfStore: store => new CorruptingObjectStore(store),
            cancellationToken: cancellationToken);
        await rig.SeedCorpusAsync(cancellationToken);

        var snapshot = VerificationLossCorpus.Snapshot(VerificationLossCorpus.Honest);
        var uploadId = await rig.ReadyUploadAsync(snapshot.Bytes, cancellationToken, VerificationLossCorpus.Consumer);
        var reportJobId = await rig.SubmitAsync(
            uploadId,
            VerificationLossCorpus.Template,
            VerificationLossCorpus.Version,
            culture: null,
            timeZone: null,
            cancellationToken,
            VerificationLossCorpus.Consumer);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        // The two rows carry the code the delivered step raises for a write that does not match.
        Assert.Equal("FAILED", job.Status);
        Assert.Equal(RefusalCode, job.ErrorCode);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal(RefusalCode, job.OutputErrorCode);

        // Nothing makes the object reachable: no part commit, no artifact, and no publication.
        Assert.Null(job.ArtifactId);
        var artifacts = await rig.Jobs.ListArtifactsAsync(reportJobId, VerificationLossCorpus.Consumer, cancellationToken);
        Assert.True(artifacts is null || artifacts.Artifacts.Count == 0, "a refused job publishes no artifact");
        Assert.Equal(0L, await rig.CountPartCommitsAsync(job.OutputId!, cancellationToken));

        // The write happened before the comparison, so its object is there - and it stays.
        var written = Assert.Single(rig.OutputObjectKeys(reportJobId, job.OutputId!));
        Assert.StartsWith($"outputs/{reportJobId}/{job.OutputId}/", written, StringComparison.Ordinal);
        Assert.NotEmpty(rig.ReadStoredObject(written));

        // The block ran once and the publication was never scheduled.
        var history = await rig.OutputHistoryAsync(reportJobId, cancellationToken);
        Assert.Single(Activities(history, PdfOutputStepContracts.RenderPdfBlock));
        Assert.Empty(Activities(history, PdfOutputActivities.PublishOutputName));
    }

    private static IReadOnlyList<HistoryEvent> Activities(WorkflowHistory history, string name) =>
        [.. history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Where(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                name,
                StringComparison.Ordinal))];

    /// <summary>
    /// The filesystem provider, with one lie: the block's own write reaches the disk as the real
    /// provider writes it, and what the worker is told about it is not what is stored.
    /// </summary>
    private sealed class CorruptingObjectStore : IObjectStore
    {
        private readonly IObjectStore _inner;

        public CorruptingObjectStore(IObjectStore inner) => _inner = inner;

        public StorageCapabilities Capabilities => _inner.Capabilities;

        public async Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken)
        {
            var written = await _inner.WriteAsync(key, content, cancellationToken);
            if (!written.Succeeded || written.Value is not { } real || !key.ToString().StartsWith("outputs/", StringComparison.Ordinal))
            {
                return written;
            }

            // One byte more than the file holds, and a hash that is not the file's: the step
            // compares both and refuses the block.
            var corrupted = real with { Size = real.Size + 1, Sha256 = Flip(real.Sha256) };
            return StorageResult.Success(corrupted);
        }

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) =>
            _inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            _inner.GetInfoAsync(key, cancellationToken);

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(key, cancellationToken);

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            _inner.ListAsync(classPrefix, cancellationToken);

        // The same hash with one hexadecimal character changed: still a well-formed hash, never
        // the file's.
        private static string Flip(string sha256) =>
            (sha256[0] == '0' ? '1' : '0') + sha256[1..];
    }
}
