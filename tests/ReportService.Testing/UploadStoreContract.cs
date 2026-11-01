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
/// to the contract fails one of them.
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
    protected static UploadRecord Session(
        string uploadId,
        string consumerId = "dev-consumer",
        UploadStatus status = UploadStatus.Created) =>
        new(
            uploadId,
            consumerId,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            status,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 4, 39, 23, 123, TimeSpan.Zero));

    [Fact]
    public async Task ATransferStartsOnceAndOnlyFromTheCreatedState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-44444444444444444444444444444444");
        await Store.CreateAsync(record, cancellationToken);

        Assert.True(await Store.TryStartTransferAsync(
            record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, cancellationToken));
        Assert.False(await Store.TryStartTransferAsync(
            record.UploadId, record.ConsumerId, UploadContentEncoding.None, cancellationToken));

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
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, cancellationToken);

        Assert.True(await Store.RecordTransferAsync(
            record.UploadId, UploadContentOutcome.Complete, 1234, 1200, Hash, cancellationToken));
        Assert.False(await Store.RecordTransferAsync(
            record.UploadId, UploadContentOutcome.Interrupted, 1, null, null, cancellationToken));
        Assert.False(await Store.RejectTransferAsync(
            record.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, 1, cancellationToken));

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
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.None, cancellationToken);

        Assert.True(await Store.RejectTransferAsync(
            record.UploadId, UploadRejectionCode.QuotaExceeded, UploadRejectionReason.StoredBytes, 64, cancellationToken));

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
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, cancellationToken);
        await Store.RecordTransferAsync(record.UploadId, UploadContentOutcome.Complete, 900, 800, Hash, cancellationToken);

        Assert.True(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, Hash, Confirmed, cancellationToken));
        Assert.False(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, null, Confirmed, cancellationToken));

        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(UploadStatus.Verifying, found!.Status);
        Assert.Equal(Confirmed, found.CompletedAt);
        Assert.Equal(Hash, found.DeclaredSha256);
    }

    [Fact]
    public async Task AConfirmationWithoutAReceivedBodyWritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-88888888888888888888888888888888");
        await Store.CreateAsync(record, cancellationToken);

        Assert.False(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, null, Confirmed, cancellationToken));
        Assert.False(await Store.ConfirmAsync(record.UploadId, "another-consumer", null, Confirmed, cancellationToken));

        var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);
        Assert.Equal(UploadStatus.Created, found!.Status);
    }

    [Fact]
    public async Task ASnapshotCommitsOncePerUploadAndOnlyWhileVerifying()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-99999999999999999999999999999999");
        await Store.CreateAsync(record, cancellationToken);
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, cancellationToken);
        await Store.RecordTransferAsync(record.UploadId, UploadContentOutcome.Complete, 900, 800, Hash, cancellationToken);

        var tooEarly = await Store.CommitReadyAsync(Snapshot(record.UploadId, "a"), cancellationToken);
        Assert.Equal(SnapshotCommitResult.NotVerifying, tooEarly);

        Assert.True(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, null, Confirmed, cancellationToken));
        Assert.Equal(SnapshotCommitResult.Committed, await Store.CommitReadyAsync(Snapshot(record.UploadId, "a"), cancellationToken));
        Assert.Equal(SnapshotCommitResult.AlreadyCommitted, await Store.CommitReadyAsync(Snapshot(record.UploadId, "b"), cancellationToken));

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
        await Store.TryStartTransferAsync(record.UploadId, record.ConsumerId, UploadContentEncoding.Gzip, cancellationToken);
        await Store.RecordTransferAsync(record.UploadId, UploadContentOutcome.Complete, 900, 800, Hash, cancellationToken);

        Assert.False(await Store.CommitRejectedAsync(
            record.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, cancellationToken));

        Assert.True(await Store.ConfirmAsync(record.UploadId, record.ConsumerId, null, Confirmed, cancellationToken));
        Assert.True(await Store.CommitRejectedAsync(
            record.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, cancellationToken));
        Assert.False(await Store.CommitRejectedAsync(
            record.UploadId, UploadRejectionCode.QuotaExceeded, UploadRejectionReason.InputBytes, cancellationToken));

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

    /// <summary>The lower-case hexadecimal hash every case of this contract uses.</summary>
    protected const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    /// <summary>The instant every confirmation of this contract records.</summary>
    protected static readonly DateTimeOffset Confirmed = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    /// <summary>A snapshot of <paramref name="uploadId"/> written under <paramref name="attempt"/>.</summary>
    /// <param name="uploadId">The upload the snapshot belongs to.</param>
    /// <param name="attempt">The attempt label the object key ends with.</param>
    protected static SnapshotRecord Snapshot(string uploadId, string attempt) => new(
        uploadId,
        "dev-consumer",
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
            "dev-consumer",
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
            var record = Session(
                "upload-" + index++.ToString("x8", CultureInfo.InvariantCulture) + new string('0', 24),
                status: status);

            await Store.CreateAsync(record, cancellationToken);
            var found = await Store.FindAsync(record.UploadId, record.ConsumerId, cancellationToken);

            Assert.NotNull(found);
            Assert.Equal(status, found!.Status);
            Assert.Equal(UploadMode.ApiStream, found.Mode);
            Assert.Equal(DataProfile.HeaderItemsV1, found.Profile);
            Assert.Equal(record.StagingKey, found.StagingKey);
        }
    }
}
