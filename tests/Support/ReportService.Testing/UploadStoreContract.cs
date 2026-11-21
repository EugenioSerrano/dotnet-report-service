using System.Globalization;
using ReportService.Api.Uploads;
using ReportService.Uploads;
using ReportService.Storage;

namespace ReportService.Testing;

/// <summary>
/// The behaviour every upload record store must show, written once and run
/// against both of them: the in-memory double the component tests use and the
/// store of the real database. Because it is the same class in both places, the
/// double cannot quietly drift away from the store it stands in for — a change
/// to the contract fails one of them. Every case decides at a fixed instant, so
/// the two ends of an upload's life are tested exactly at their boundaries.
/// </summary>
public abstract class UploadStoreContract : IAsyncLifetime
{
    /// <summary>The store under test, created by <see cref="CreateStoreAsync"/> before the first case runs.</summary>
    protected IUploadStore Store { get; private set; } = null!;

    /// <summary>Creates the store this class's cases run against.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected abstract Task<IUploadStore> CreateStoreAsync(CancellationToken cancellationToken);

    /// <summary>Releases what <see cref="CreateStoreAsync"/> created, when the store holds anything.</summary>
    /// <param name="store">The store created for the class.</param>
    protected virtual ValueTask DisposeStoreAsync(IUploadStore store) => default;

    /// <summary>Creates a stored session with every value a caller can distinguish.</summary>
    /// <param name="uploadId">Its identifier.</param>
    /// <param name="consumerId">Its owner.</param>
    /// <param name="status">Its state.</param>
    /// <param name="expiresAt">When its session ends; by default a day after the instant these cases decide at.</param>
    protected static UploadRecord Session(
        string uploadId,
        string consumerId = Consumer,
        UploadStatus status = UploadStatus.Created,
        DateTimeOffset? expiresAt = null) =>
        new(
            uploadId,
            consumerId,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            status,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero),
            expiresAt ?? new DateTimeOffset(2026, 11, 2, 4, 39, 23, 123, TimeSpan.Zero));

    [Fact]
    public async Task ATransferStartsOnceAndOnlyFromTheCreatedState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-44444444444444444444444444444444");
        await Store.CreateAsync(record, cancellationToken);

        Assert.True(await Store.TryStartTransferAsync(
            record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, Now, cancellationToken));
        Assert.False(await Store.TryStartTransferAsync(
            record.UploadId, record.ConsumerId, UploadContentEncoding.None, Now, cancellationToken));

        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(UploadStatus.Uploading, found!.Status);
        Assert.Equal(UploadContentEncoding.Gzip, found.ContentEncoding);
    }

    [Fact]
    public async Task TheOutcomeIsRecordedOnceAndOnlyWhileReceiving()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-55555555555555555555555555555555");
        await Store.CreateAsync(record, cancellationToken);
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, Now, cancellationToken);

        Assert.True(await Store.RecordTransferAsync(
            record.UploadId, UploadContentOutcome.Complete, 1234, 1200, Hash, Now, cancellationToken));
        Assert.False(await Store.RecordTransferAsync(
            record.UploadId, UploadContentOutcome.Interrupted, 1, null, null, Now, cancellationToken));
        Assert.False(await Store.RejectTransferAsync(
            record.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, 1, Now, cancellationToken));

        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(UploadContentOutcome.Complete, found!.ContentOutcome);
        Assert.Equal(1234, found.ReceivedBytes);
        Assert.Equal(1200, found.StagingSize);
        Assert.Equal(Hash, found.StagingSha256);
        Assert.Equal(UploadStatus.Uploading, found.Status);
    }

    [Fact]
    public async Task ATransferStoppedAtTheLimitEndsTheSessionRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-66666666666666666666666666666666");
        await Store.CreateAsync(record, cancellationToken);
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.None, Now, cancellationToken);

        Assert.True(await Store.RejectTransferAsync(
            record.UploadId, UploadRejectionCode.QuotaExceeded, UploadRejectionReason.StoredBytes, 64, Now, cancellationToken));

        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(UploadStatus.Rejected, found!.Status);
        Assert.Equal(UploadRejectionCode.QuotaExceeded, found.RejectionCode);
        Assert.Equal(UploadRejectionReason.StoredBytes, found.RejectionReason);
        Assert.Equal(64, found.ReceivedBytes);
    }

    [Fact]
    public async Task ConfirmationMovesTheUploadToVerifyingAndIsRefusedAfterwards()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-77777777777777777777777777777777");
        await Store.CreateAsync(record, cancellationToken);
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, Now, cancellationToken);
        await Store.RecordTransferAsync(record.UploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now, cancellationToken);

        Assert.True(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, Hash, Confirmed, Now, Deadline, cancellationToken));
        Assert.False(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, null, Confirmed, Now, Deadline, cancellationToken));

        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(UploadStatus.Verifying, found!.Status);
        Assert.Equal(Confirmed, found.CompletedAt);
        Assert.Equal(Deadline, found.VerificationDeadline);
        Assert.Equal(Hash, found.DeclaredSha256);
    }

    [Fact]
    public async Task AConfirmationWithoutAReceivedBodyWritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-88888888888888888888888888888888");
        await Store.CreateAsync(record, cancellationToken);

        Assert.False(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, null, Confirmed, Now, Deadline, cancellationToken));
        Assert.False(await Store.ConfirmAsync(record.UploadId, "another-consumer", null, Confirmed, Now, Deadline, cancellationToken));

        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);
        Assert.Equal(UploadStatus.Created, found!.Status);
    }

    [Fact]
    public async Task ASnapshotCommitsOncePerUploadAndOnlyWhileVerifying()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-99999999999999999999999999999999");
        await Store.CreateAsync(record, cancellationToken);
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, Now, cancellationToken);
        await Store.RecordTransferAsync(record.UploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now, cancellationToken);

        var tooEarly = await Store.CommitReadyAsync(Snapshot(record.UploadId, "a"), Now, cancellationToken);
        Assert.Equal(SnapshotCommitResult.NotVerifying, tooEarly);

        Assert.True(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, null, Confirmed, Now, Deadline, cancellationToken));
        Assert.Equal(SnapshotCommitResult.Committed, await Store.CommitReadyAsync(Snapshot(record.UploadId, "a"), Now, cancellationToken));
        Assert.Equal(SnapshotCommitResult.AlreadyCommitted, await Store.CommitReadyAsync(Snapshot(record.UploadId, "b"), Now, cancellationToken));

        var found = await Store.LoadForVerificationAsync(record.UploadId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(UploadStatus.Ready, found!.Status);
        Assert.NotNull(found.Snapshot);
        Assert.Equal(ObjectKey.FromSegments("snapshots", record.UploadId, "a"), found.Snapshot!.ObjectKey);
    }

    [Fact]
    public async Task ARejectionCommitsOnlyWhileVerifying()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await Store.CreateAsync(record, cancellationToken);
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, Now, cancellationToken);
        await Store.RecordTransferAsync(record.UploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now, cancellationToken);

        Assert.False(await Store.CommitRejectedAsync(
            record.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, Now, cancellationToken));

        Assert.True(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, null, Confirmed, Now, Deadline, cancellationToken));
        Assert.True(await Store.CommitRejectedAsync(
            record.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, Now, cancellationToken));
        Assert.False(await Store.CommitRejectedAsync(
            record.UploadId, UploadRejectionCode.QuotaExceeded, UploadRejectionReason.InputBytes, Now, cancellationToken));

        var found = await Store.LoadForVerificationAsync(record.UploadId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(UploadStatus.Rejected, found!.Status);
        Assert.Equal(UploadRejectionReason.Truncated, found.RejectionReason);
    }

    [Fact]
    public async Task AnUnknownUploadVerifiesAsNothing()
    {
        Assert.Null(await Store.LoadForVerificationAsync(
            "upload-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EveryWriteIsRefusedAtTheEndOfTheSessionAndWorksJustBeforeIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var justBefore = Session("upload-c0000000000000000000000000000001", expiresAt: Now.AddMilliseconds(1));
        var atTheEnd = Session("upload-c0000000000000000000000000000002", expiresAt: Now);
        await Store.CreateAsync(justBefore, cancellationToken);
        await Store.CreateAsync(atTheEnd, cancellationToken);

        // A millisecond before the end, every step still behaves as it always does.
        Assert.True(await Store.TryStartTransferAsync(
            justBefore.UploadId, justBefore.ConsumerId, UploadContentEncoding.None, Now, cancellationToken));
        Assert.True(await Store.RecordTransferAsync(
            justBefore.UploadId, UploadContentOutcome.Complete, 10, 10, Hash, Now, cancellationToken));
        Assert.True(await Store.ConfirmAsync(
            justBefore.UploadId, justBefore.ConsumerId, null, Confirmed, Now, Deadline, cancellationToken));

        // At its expiry exactly, the upload accepts nothing: it was not confirmed before it.
        Assert.False(await Store.TryStartTransferAsync(
            atTheEnd.UploadId, atTheEnd.ConsumerId, UploadContentEncoding.None, Now, cancellationToken));
        Assert.False(await Store.RecordTransferAsync(
            atTheEnd.UploadId, UploadContentOutcome.Complete, 10, 10, Hash, Now, cancellationToken));
        Assert.False(await Store.ConfirmAsync(
            atTheEnd.UploadId, atTheEnd.ConsumerId, null, Confirmed, Now, Deadline, cancellationToken));

        var endsRejected = Session("upload-c0000000000000000000000000000003");
        await Store.CreateAsync(endsRejected, cancellationToken);
        Assert.True(await Store.TryStartTransferAsync(
            endsRejected.UploadId, endsRejected.ConsumerId, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken));
        Assert.False(await Store.RejectTransferAsync(
            endsRejected.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, 10, endsRejected.ExpiresAt, cancellationToken));

        var found = await Store.FindAsync(atTheEnd.UploadId, atTheEnd.ConsumerId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(UploadStatus.Created, found!.Status);
        Assert.Null(found.ContentOutcome);
    }

    [Fact]
    public async Task EveryCommitIsRefusedAtTheVerificationDeadline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ready = Session("upload-d0000000000000000000000000000001");
        var late = Session("upload-d0000000000000000000000000000003");
        var rejected = Session("upload-d0000000000000000000000000000002");
        await Store.CreateAsync(ready, cancellationToken);
        await Store.CreateAsync(late, cancellationToken);
        await Store.CreateAsync(rejected, cancellationToken);
        foreach (var record in new[] { ready, late, rejected })
        {
            Assert.True(await Store.TryStartTransferAsync(
                record.UploadId, record.ConsumerId, UploadContentEncoding.None, Now, cancellationToken));
            Assert.True(await Store.RecordTransferAsync(
                record.UploadId, UploadContentOutcome.Complete, 10, 10, Hash, Now, cancellationToken));
            Assert.True(await Store.ConfirmAsync(
                record.UploadId, record.ConsumerId, null, Confirmed, Now, Deadline, cancellationToken));
        }

        // Just before the deadline the commit still wins; at it, the upload is expired by
        // the same instant and the commit writes nothing.
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await Store.CommitReadyAsync(Snapshot(ready.UploadId, "a"), Deadline.AddMilliseconds(-1), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.NotVerifying,
            await Store.CommitReadyAsync(Snapshot(late.UploadId, "a"), Deadline, cancellationToken));
        Assert.False(await Store.CommitRejectedAsync(
            rejected.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, Deadline, cancellationToken));
    }

    [Fact]
    public async Task ExpiringOneUploadIsANoOpBeforeTheEndAndIdempotentAtIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-e0000000000000000000000000000001");
        await Store.CreateAsync(record, cancellationToken);

        var early = await Store.ExpireIfOverdueAsync(record.UploadId, Now, cancellationToken);
        Assert.False(early.Expired);
        Assert.Equal(UploadStatus.Created, early.Upload!.Status);

        var expired = await Store.ExpireIfOverdueAsync(record.UploadId, record.ExpiresAt, cancellationToken);
        Assert.True(expired.Expired);
        Assert.Equal(UploadStatus.Expired, expired.Upload!.Status);

        var again = await Store.ExpireIfOverdueAsync(record.UploadId, record.ExpiresAt, cancellationToken);
        Assert.False(again.Expired);
        Assert.Equal(UploadStatus.Expired, again.Upload!.Status);

        var missing = await Store.ExpireIfOverdueAsync(
            "upload-e0000000000000000000000000000002", record.ExpiresAt, cancellationToken);
        Assert.False(missing.Expired);
        Assert.Null(missing.Upload);
    }

    [Fact]
    public async Task TheBatchExpiryNamesWhatItExpiredAndLeavesTheRestAlone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var overdue = new[]
        {
            Session("upload-f0000000000000000000000000000001", expiresAt: Now),
            Session("upload-f0000000000000000000000000000002", expiresAt: Now),
            Session("upload-f0000000000000000000000000000003", expiresAt: Now),
        };
        var untouched = Session("upload-f0000000000000000000000000000009");
        foreach (var record in overdue)
        {
            await Store.CreateAsync(record, cancellationToken);
        }

        await Store.CreateAsync(untouched, cancellationToken);

        var first = await Store.ExpireOverdueAsync(Now, 2, cancellationToken);
        Assert.Equal(2, first.Count);
        Assert.Equal(
            overdue.Take(2).Select(record => record.UploadId),
            first.Select(expired => expired.UploadId));
        Assert.All(first, expired =>
        {
            Assert.Equal(UploadStatus.Created, expired.PreviousStatus);
            Assert.Equal(UploadExpiry.SessionCause, expired.Cause);
        });
        Assert.Equal(
            ObjectKey.FromSegments("staging", overdue[0].UploadId, "content"),
            first[0].StagingKey);

        var second = await Store.ExpireOverdueAsync(Now, 2, cancellationToken);
        Assert.Equal(new[] { overdue[2].UploadId }, second.Select(expired => expired.UploadId).ToArray());

        var third = await Store.ExpireOverdueAsync(Now, 2, cancellationToken);
        Assert.Empty(third);

        var found = await Store.FindAsync(untouched.UploadId, untouched.ConsumerId, cancellationToken);
        Assert.Equal(UploadStatus.Created, found!.Status);
    }

    [Fact]
    public async Task TheBatchExpiryNamesTheStateAnUploadWasInBeforeItExpired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // One upload is still receiving when its session ends; the other was confirmed and
        // outlasts the deadline of its verification instead.
        var uploading = Session("upload-b0000000000000000000000000000001", expiresAt: Now.AddHours(1));
        var verifying = Session("upload-b0000000000000000000000000000002", expiresAt: Now.AddDays(1));
        await Store.CreateAsync(uploading, cancellationToken);
        await Store.CreateAsync(verifying, cancellationToken);

        Assert.True(await Store.TryStartTransferAsync(
            uploading.UploadId, uploading.ConsumerId, UploadContentEncoding.None, Now, cancellationToken));
        Assert.True(await Store.TryStartTransferAsync(
            verifying.UploadId, verifying.ConsumerId, UploadContentEncoding.None, Now, cancellationToken));
        Assert.True(await Store.RecordTransferAsync(
            verifying.UploadId, UploadContentOutcome.Complete, 10, 10, Hash, Now, cancellationToken));
        Assert.True(await Store.ConfirmAsync(
            verifying.UploadId, verifying.ConsumerId, null, Confirmed, Now, Now.AddHours(1), cancellationToken));

        var expired = await Store.ExpireOverdueAsync(Now.AddHours(2), 10, cancellationToken);

        Assert.Equal(
            new[] { uploading.UploadId, verifying.UploadId },
            expired.Select(upload => upload.UploadId).ToArray());
        Assert.Equal(UploadStatus.Uploading, expired[0].PreviousStatus);
        Assert.Equal(UploadExpiry.SessionCause, expired[0].Cause);
        Assert.Equal(UploadStatus.Verifying, expired[1].PreviousStatus);
        Assert.Equal(UploadExpiry.VerificationDeadlineCause, expired[1].Cause);
    }

    /// <summary>The consumer every case of this contract records sessions for.</summary>
    public const string Consumer = "dev-consumer";

    /// <summary>The lower-case hexadecimal hash every case of this contract uses.</summary>
    protected const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    /// <summary>The instant every confirmation of this contract records.</summary>
    protected static readonly DateTimeOffset Confirmed = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    /// <summary>The instant every write of this contract is decided at.</summary>
    protected static readonly DateTimeOffset Now = Confirmed;

    /// <summary>The verification deadline every confirmation of this contract records.</summary>
    protected static readonly DateTimeOffset Deadline = Confirmed.AddHours(6);

    /// <summary>A snapshot of <paramref name="uploadId"/> written under <paramref name="attempt"/>.</summary>
    /// <param name="uploadId">The upload the snapshot belongs to.</param>
    /// <param name="attempt">The attempt label the object key ends with.</param>
    protected static SnapshotRecord Snapshot(string uploadId, string attempt) => new(
        uploadId,
        Consumer,
        ObjectKey.FromSegments("snapshots", uploadId, attempt),
        UploadContentEncoding.Gzip,
        DataProfile.HeaderItemsV1,
        Hash,
        900,
        Hash,
        800,
        Confirmed);

    /// <inheritdoc />
    public async ValueTask InitializeAsync() =>
        Store = await CreateStoreAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Store is not null)
        {
            await DisposeStoreAsync(Store);
        }
    }

    [Fact]
    public async Task CreatedSessionIsFoundByItsOwnerWithEveryValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-0123456789abcdef0123456789abcdef");

        await Store.CreateAsync(record, cancellationToken);
        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);

        Assert.Equal(record, found);
    }

    [Fact]
    public async Task SessionOfAnotherConsumerIsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-11111111111111111111111111111111");

        await Store.CreateAsync(record, cancellationToken);

        Assert.Null(await Store.FindAsync(record.UploadId, "another-consumer", cancellationToken));
    }

    [Fact]
    public async Task UnknownSessionIsNotFound()
    {
        Assert.Null(await Store.FindAsync(
            "upload-22222222222222222222222222222222",
            Consumer,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TimestampsKeepTheirValueToTheMillisecond()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-33333333333333333333333333333333");

        await Store.CreateAsync(record, cancellationToken);
        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);

        Assert.NotNull(found);
        Assert.Equal(record.CreatedAt, found!.CreatedAt);
        Assert.Equal(record.ExpiresAt, found.ExpiresAt);
        Assert.Equal(TimeSpan.Zero, found.CreatedAt.Offset);
    }

    [Fact]
    public async Task EveryStatusRoundTrips()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var index = 0;
        foreach (var status in Enum.GetValues<UploadStatus>())
        {
            // Every state is reached the way the service reaches it: the session is created
            // in its first state and moved on by the step that owns the transition, which
            // sets the fields that state owns (the instant a confirmation happened and the
            // deadline it carries, the code and reason a rejection carries, the expiry of a
            // session that ran out).
            var uploadId = "upload-" + index++.ToString("x8", CultureInfo.InvariantCulture) + new string('0', 24);
            var record = Session(uploadId, status: ReachedByCreation(status) ? status : UploadStatus.Created);

            await Store.CreateAsync(record, cancellationToken);
            await DriveToAsync(uploadId, status, cancellationToken);
            var found = await Store.FindAsync(uploadId, record.ConsumerId, cancellationToken);

            Assert.NotNull(found);
            Assert.Equal(status, found!.Status);
            Assert.Equal(UploadMode.ApiStream, found.Mode);
            Assert.Equal(DataProfile.HeaderItemsV1, found.Profile);
            Assert.Equal(record.StagingKey, found.StagingKey);
        }
    }

    /// <summary>Whether a session is created in <paramref name="status"/> rather than moved into it.</summary>
    private static bool ReachedByCreation(UploadStatus status) => status == UploadStatus.Created;

    /// <summary>Moves a created session to <paramref name="status"/> through the store's own steps.</summary>
    private async Task DriveToAsync(string uploadId, UploadStatus status, CancellationToken cancellationToken)
    {
        if (status is UploadStatus.Created)
        {
            return;
        }

        Assert.True(await Store.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.Gzip, Now, cancellationToken));
        if (status is UploadStatus.Uploading)
        {
            return;
        }

        if (status is UploadStatus.Rejected)
        {
            // A transfer the service stops at its limit ends the session before any
            // outcome is recorded, which is the only way that step admits.
            Assert.True(await Store.RejectTransferAsync(
                uploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, 900, Now, cancellationToken));
            return;
        }

        Assert.True(await Store.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now, cancellationToken));
        switch (status)
        {
            case UploadStatus.Verifying:
                Assert.True(await Store.ConfirmAsync(uploadId, Consumer, Hash, Confirmed, Now, Deadline, cancellationToken));
                break;
            case UploadStatus.Ready:
                Assert.True(await Store.ConfirmAsync(uploadId, Consumer, Hash, Confirmed, Now, Deadline, cancellationToken));
                Assert.Equal(SnapshotCommitResult.Committed, await Store.CommitReadyAsync(Snapshot(uploadId, "a"), Now, cancellationToken));
                break;
            case UploadStatus.Expired:
                Assert.True(await Store.ConfirmAsync(uploadId, Consumer, Hash, Confirmed, Now, Deadline, cancellationToken));
                var expired = await Store.ExpireIfOverdueAsync(uploadId, Deadline, cancellationToken);
                Assert.True(expired.Expired);
                break;
            default:
                throw new InvalidOperationException($"the contract has no step to {status}");
        }
    }
}
