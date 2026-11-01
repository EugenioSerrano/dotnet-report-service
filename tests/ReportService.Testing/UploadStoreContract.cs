using System.Globalization;
using ReportService.Api.Uploads;
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
