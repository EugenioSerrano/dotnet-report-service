using System.Net;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// When an upload ends, through the API's real pipeline with a clock the test moves:
/// an upload that is not confirmed before the end of its session expires and takes
/// no more content and no confirmation, and one whose verification outlasts its own
/// deadline expires as well and can never become a snapshot afterwards. Both answers
/// are exact at the boundary, and both are shown by the record, not only by the answer.
/// A body that is still arriving when the session's end passes is answered by the
/// state the upload is in, and keeps nothing of what it wrote.
/// </summary>
public sealed class UploadExpiryEndpointsTests
{
    private static readonly (string ConsumerId, bool Active)[] Consumers = [("dev-consumer", true)];

    private static readonly TimeSpan Deadline = TimeSpan.FromHours(6);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUploadNotConfirmedInTimeExpiresAtTheEndOfItsSessionAndLosesItsStagingObject()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        using var sent = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        host.Clock.Advance(host.Lifetime);

        using var status = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(status));

        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(UploadStatus.Expired, record!.Status);
        var staging = await host.Objects.GetInfoAsync(record.StagingKey, Cancellation);
        Assert.Equal(StorageOutcome.NotFound, staging.Outcome);
        Assert.Contains(host.Logs.Lines, line => line.Contains("expired (session)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATransferWhoseSessionEndsBeforeItsRecordIsAnsweredExpiredAndKeepsNothing()
    {
        var clock = new ManualTimeProvider(UploadApiHost.Now);
        var writer = new ExpiringBeforeTheRecord(clock, "dev-consumer");
        using var host = new UploadApiHost(Consumers, clock: clock, store: writer.Wrap);
        var uploadId = await host.CreateAsync();

        using var response = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);

        // The session's end passed while the body was still arriving: the transfer had already
        // started, and its record could no longer be written.
        Assert.Equal(UploadStatus.Uploading, writer.StatusWhenEnded);
        Assert.Equal(1, writer.Expiries);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await UploadApiHost.ProblemAsync(response);
        Assert.Contains(ErrorCodes.UploadStateConflict, problem, StringComparison.Ordinal);
        Assert.Contains(UploadEndpoints.ExpiredDetail, problem, StringComparison.Ordinal);

        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(UploadStatus.Expired, record!.Status);
        Assert.Null(record.ContentOutcome);
        var staging = await host.Objects.GetInfoAsync(record.StagingKey, Cancellation);
        Assert.Equal(StorageOutcome.NotFound, staging.Outcome);
    }

    [Fact]
    public async Task AnExpiredUploadTakesNoContentAndNoConfirmation()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        host.Clock.Advance(host.Lifetime);

        using var content = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        using var confirm = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Contains(ErrorCodes.UploadStateConflict, await UploadApiHost.ProblemAsync(content), StringComparison.Ordinal);
        Assert.Contains(UploadEndpoints.ExpiredDetail, await UploadApiHost.ProblemAsync(content), StringComparison.Ordinal);
        Assert.Contains(UploadEndpoints.ExpiredDetail, await UploadApiHost.ProblemAsync(confirm), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JustBeforeTheEndEveryOperationStillWorks()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        host.Clock.Advance(host.Lifetime - TimeSpan.FromMilliseconds(1));

        using var content = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        using var confirm = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(UploadStatus.Verifying, record!.Status);
    }

    [Fact]
    public async Task TheConfirmationRecordsTheDeadlineAndTheUploadExpiresByIt()
    {
        using var host = new UploadApiHost(Consumers, sessionLifetime: "01:00:00");
        var uploadId = await host.CreateAsync();
        using var sent = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        using var confirm = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);

        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(UploadStatus.Verifying, record!.Status);
        Assert.Equal(record.CompletedAt + Deadline, record.VerificationDeadline);

        // A confirming session that ended long ago does not bound the verification.
        host.Clock.Advance(host.Lifetime);
        using var beforeDeadline = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("verifying", await UploadApiHost.StatusOfAsync(beforeDeadline));

        host.Clock.Advance(Deadline - host.Lifetime - TimeSpan.FromMilliseconds(1));
        using var justBefore = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("verifying", await UploadApiHost.StatusOfAsync(justBefore));

        host.Clock.Advance(TimeSpan.FromMilliseconds(2));
        using var atDeadline = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(atDeadline));
        Assert.Equal(
            UploadStatus.Expired,
            (await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation))!.Status);
        Assert.Contains(host.Logs.Lines, line => line.Contains("expired (verification-deadline)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARepeatedConfirmationOfAnUploadThatExpiredWhileVerifyingAnswersItsState()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        using var sent = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        using var confirmed = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        var declared = record!.DeclaredSha256;

        host.Clock.Advance(Deadline);
        using var expired = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(expired));
        var before = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        var intents = host.Store.Intents.Count;

        using var repeated = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(repeated));
        var after = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(before, after);
        Assert.Equal(intents, host.Store.Intents.Count);

        // A declaration that differs from the one this upload was confirmed with is refused.
        var other = new string('a', 64);
        Assert.NotEqual(other, declared, StringComparer.OrdinalIgnoreCase);
        using var different = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "dev-consumer", $$"""{"sha256":"{{other}}"}"""), Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Contains(ErrorCodes.UploadStateConflict, await UploadApiHost.ProblemAsync(different), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedConfirmationWithTheSameDeclarationStillAnswersTheExpiredState()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        var sha256 = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";
        using var sent = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        using var confirmed = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "dev-consumer", $$"""{"sha256":"{{sha256}}"}"""), Cancellation);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);

        host.Clock.Advance(Deadline);
        using var expired = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(expired));

        using var repeated = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "dev-consumer", $$"""{"sha256":"{{sha256}}"}"""), Cancellation);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(repeated));
    }

    /// <summary>
    /// The upload records the content operation writes through, with the session's end
    /// arriving in the middle of a transfer: the upload is expired at the one instant the
    /// transfer is about to record how it ended, which is what the sweep, another replica
    /// or a concurrent request does. Every call but that one is the delivered double's own
    /// answer, and the expiry is the store's own rule, not a status written by hand.
    /// </summary>
    private sealed class ExpiringBeforeTheRecord(ManualTimeProvider clock, string consumerId)
    {
        private int _expiries;

        /// <summary>How many transfers the session's end met before their record.</summary>
        public int Expiries => Volatile.Read(ref _expiries);

        /// <summary>The state the upload was in when its session's end passed.</summary>
        public UploadStatus? StatusWhenEnded { get; private set; }

        /// <summary>Wraps the store the host is given.</summary>
        /// <param name="inner">The store of the host.</param>
        public IUploadStore Wrap(IUploadStore inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            return new Store(this, inner);
        }

        // The session's end passes just before the transfer's own record is written.
        private async Task EndTheSessionAsync(IUploadStore inner, string uploadId, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _expiries, 1) != 0)
            {
                return;
            }

            var record = await inner.FindAsync(uploadId, consumerId, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return;
            }

            StatusWhenEnded = record.Status;
            clock.Advance(record.ExpiresAt - clock.GetUtcNow());
            await inner.ExpireIfOverdueAsync(uploadId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        private sealed class Store(ExpiringBeforeTheRecord owner, IUploadStore inner) : IUploadStore
        {
            public Task CreateAsync(UploadRecord record, CancellationToken cancellationToken) =>
                inner.CreateAsync(record, cancellationToken);

            public Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken) =>
                inner.FindAsync(uploadId, consumerId, cancellationToken);

            public Task<bool> TryStartTransferAsync(
                string uploadId,
                string consumerId,
                UploadContentEncoding encoding,
                DateTimeOffset now,
                CancellationToken cancellationToken) =>
                inner.TryStartTransferAsync(uploadId, consumerId, encoding, now, cancellationToken);

            public async Task<bool> RecordTransferAsync(
                string uploadId,
                UploadContentOutcome outcome,
                long receivedBytes,
                long? stagingSize,
                string? stagingSha256,
                DateTimeOffset now,
                CancellationToken cancellationToken)
            {
                await owner.EndTheSessionAsync(inner, uploadId, cancellationToken).ConfigureAwait(false);
                return await inner
                    .RecordTransferAsync(uploadId, outcome, receivedBytes, stagingSize, stagingSha256, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            public Task<bool> RejectTransferAsync(
                string uploadId,
                UploadRejectionCode code,
                UploadRejectionReason reason,
                long receivedBytes,
                DateTimeOffset now,
                CancellationToken cancellationToken) =>
                inner.RejectTransferAsync(uploadId, code, reason, receivedBytes, now, cancellationToken);

            public Task<bool> ConfirmAsync(
                string uploadId,
                string consumerId,
                string? declaredSha256,
                DateTimeOffset completedAt,
                DateTimeOffset now,
                DateTimeOffset verificationDeadline,
                CancellationToken cancellationToken) =>
                inner.ConfirmAsync(uploadId, consumerId, declaredSha256, completedAt, now, verificationDeadline, cancellationToken);

            public Task<UploadExpiryOutcome> ExpireIfOverdueAsync(string uploadId, DateTimeOffset now, CancellationToken cancellationToken) =>
                inner.ExpireIfOverdueAsync(uploadId, now, cancellationToken);

            public Task<IReadOnlyList<ExpiredUpload>> ExpireOverdueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
                inner.ExpireOverdueAsync(now, batchSize, cancellationToken);

            public Task<UploadRecord?> LoadForVerificationAsync(string uploadId, CancellationToken cancellationToken) =>
                inner.LoadForVerificationAsync(uploadId, cancellationToken);

            public Task<SnapshotCommitResult> CommitReadyAsync(SnapshotRecord snapshot, DateTimeOffset now, CancellationToken cancellationToken) =>
                inner.CommitReadyAsync(snapshot, now, cancellationToken);

            public Task<bool> CommitRejectedAsync(
                string uploadId,
                UploadRejectionCode code,
                UploadRejectionReason reason,
                DateTimeOffset now,
                CancellationToken cancellationToken) =>
                inner.CommitRejectedAsync(uploadId, code, reason, now, cancellationToken);
        }
    }
}
