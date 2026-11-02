using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// What a verification does when it meets its upload's deadline: an attempt that
/// starts after it writes nothing and expires the upload, and an attempt whose
/// commit is refused because the deadline passed in the meantime writes no snapshot
/// record and leaves neither its own object nor the upload's staging object behind.
/// </summary>
public sealed class UploadVerifierDeadlineTests
{
    private const string UploadId = "upload-0123456789abcdef0123456789abcdef";

    private static readonly byte[] Stored = Encoding.UTF8.GetBytes("""{"items":[]}""");

    private static readonly DateTimeOffset Deadline = new(2026, 11, 1, 5, 11, 2, 456, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAttemptThatStartsAfterTheDeadlineWritesNothingAndExpiresTheUpload()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(
            UploadId, Stored, UploadContentEncoding.None, verificationDeadline: Deadline);

        rig.Clock.Advance(TimeSpan.FromHours(1));

        var outcome = await rig.Verifier.VerifyAsync(UploadId, VerifierRig.AttemptKey(UploadId), null, Cancellation);

        Assert.Equal(VerificationResult.NotVerifying, outcome.Result);
        Assert.Null(rig.Uploads.SnapshotOf(UploadId));
        Assert.Empty(await rig.ListAsync($"snapshots/{UploadId}"));
        Assert.Empty(await rig.ListAsync($"staging/{UploadId}"));
        Assert.Equal(UploadStatus.Expired, (await rig.Uploads.LoadForVerificationAsync(UploadId, Cancellation))!.Status);
    }

    [Fact]
    public async Task ACommitRefusedByTheDeadlineLeavesNoSnapshotAndDeletesBothObjects()
    {
        using var rig = new VerifierRig();
        await rig.GivenVerifyingAsync(UploadId, Stored, UploadContentEncoding.None);
        using var metrics = new OrchestrationMetrics();
        var lateStore = new ExpiredBeforeCommitStore(rig.Uploads, rig.Clock.GetUtcNow().AddDays(2));
        var verifier = new UploadVerifier(
            lateStore,
            rig.Objects,
            Options.Create(rig.Options),
            rig.Clock,
            NullLogger<UploadVerifier>.Instance,
            metrics);

        var outcome = await verifier.VerifyAsync(UploadId, VerifierRig.AttemptKey(UploadId), null, Cancellation);

        Assert.Equal(VerificationResult.NotVerifying, outcome.Result);
        Assert.Null(rig.Uploads.SnapshotOf(UploadId));
        Assert.Empty(await rig.ListAsync($"snapshots/{UploadId}"));
        Assert.Empty(await rig.ListAsync($"staging/{UploadId}"));
        Assert.Equal(UploadStatus.Expired, (await rig.Uploads.LoadForVerificationAsync(UploadId, Cancellation))!.Status);
    }

    /// <summary>
    /// The upload records of the rig, with the sweep winning the race: the attempt
    /// writes its object and finds its upload expired by the time it commits.
    /// </summary>
    private sealed class ExpiredBeforeCommitStore(IUploadStore inner, DateTimeOffset expiredAt) : IUploadStore
    {
        public Task CreateAsync(UploadRecord record, CancellationToken cancellationToken) =>
            inner.CreateAsync(record, cancellationToken);

        public Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken) =>
            inner.FindAsync(uploadId, consumerId, cancellationToken);

        public Task<bool> TryStartTransferAsync(string uploadId, string consumerId, UploadContentEncoding encoding, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.TryStartTransferAsync(uploadId, consumerId, encoding, now, cancellationToken);

        public Task<bool> RecordTransferAsync(string uploadId, UploadContentOutcome outcome, long receivedBytes, long? stagingSize, string? stagingSha256, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.RecordTransferAsync(uploadId, outcome, receivedBytes, stagingSize, stagingSha256, now, cancellationToken);

        public Task<bool> RejectTransferAsync(string uploadId, UploadRejectionCode code, UploadRejectionReason reason, long receivedBytes, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.RejectTransferAsync(uploadId, code, reason, receivedBytes, now, cancellationToken);

        public Task<bool> ConfirmAsync(string uploadId, string consumerId, string? declaredSha256, DateTimeOffset completedAt, DateTimeOffset now, DateTimeOffset verificationDeadline, CancellationToken cancellationToken) =>
            inner.ConfirmAsync(uploadId, consumerId, declaredSha256, completedAt, now, verificationDeadline, cancellationToken);

        public Task<UploadExpiryOutcome> ExpireIfOverdueAsync(string uploadId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ExpireIfOverdueAsync(uploadId, now, cancellationToken);

        public Task<IReadOnlyList<ExpiredUpload>> ExpireOverdueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
            inner.ExpireOverdueAsync(now, batchSize, cancellationToken);

        public Task<UploadRecord?> LoadForVerificationAsync(string uploadId, CancellationToken cancellationToken) =>
            inner.LoadForVerificationAsync(uploadId, cancellationToken);

        public async Task<SnapshotCommitResult> CommitReadyAsync(SnapshotRecord snapshot, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var expired = await inner.ExpireIfOverdueAsync(snapshot.UploadId, expiredAt, cancellationToken);
            Assert.True(expired.Expired, "the upload had to be expired for the commit to lose its race");
            return SnapshotCommitResult.NotVerifying;
        }

        public Task<bool> CommitRejectedAsync(string uploadId, UploadRejectionCode code, UploadRejectionReason reason, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.CommitRejectedAsync(uploadId, code, reason, now, cancellationToken);
    }
}
