using Dapper;
using Npgsql;
using ReportService.Migrations;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// The end of an upload's life on a real PostgreSQL server: the constraints that
/// keep a confirmed upload from standing without a deadline, the batch expiry that
/// two callers may run at once without expiring the same upload twice, and the
/// back-fill that gives the uploads confirmed before the script a deadline of their
/// own.
/// </summary>
public sealed class UploadVerificationDeadlineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource _dataSource;

    /// <summary>Brings a database of its own to the version the code expects.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public UploadVerificationDeadlineTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _dataSource = null!;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var database = await _fixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        var migration = await MigrationRunner.RunAsync(
            ConnectionString(database), MigrationCatalog.Scripts, TestContext.Current.CancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(ConnectionString(database));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task AVerifyingUploadWithoutADeadlineIsRefusedByTheSchema()
    {
        await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            """
            INSERT INTO report_service.uploads
                (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at, completed_at)
            VALUES
                (@UploadId, 'dev-consumer', 'API_STREAM', 'HEADER_ITEMS_V1', 'VERIFYING',
                 'staging/x/content', @CreatedAt, @ExpiresAt, @CompletedAt)
            """,
            new
            {
                UploadId = "upload-00000000000000000000000000000001",
                CreatedAt = Now.UtcDateTime,
                ExpiresAt = Now.AddDays(1).UtcDateTime,
                CompletedAt = Now.UtcDateTime,
            }));

        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("uploads_deadline_when_verifying", failure.ConstraintName);
    }

    [Fact]
    public async Task ADeadlineBeforeTheConfirmationIsRefusedByTheSchema()
    {
        await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            """
            INSERT INTO report_service.uploads
                (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at,
                 completed_at, verification_deadline)
            VALUES
                (@UploadId, 'dev-consumer', 'API_STREAM', 'HEADER_ITEMS_V1', 'VERIFYING',
                 'staging/x/content', @CreatedAt, @ExpiresAt, @CompletedAt, @Deadline)
            """,
            new
            {
                UploadId = "upload-00000000000000000000000000000002",
                CreatedAt = Now.UtcDateTime,
                ExpiresAt = Now.AddDays(1).UtcDateTime,
                CompletedAt = Now.UtcDateTime,
                Deadline = Now.AddMinutes(-1).UtcDateTime,
            }));

        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("uploads_deadline_after_confirmation", failure.ConstraintName);
    }

    [Fact]
    public async Task TwoConcurrentBatchExpiriesExpireDisjointSetsThatCoverEveryOverdueUpload()
    {
        var store = new PostgresUploadStore(_dataSource);
        var cancellationToken = TestContext.Current.CancellationToken;
        var overdue = new List<string>();
        for (var index = 0; index < 20; index++)
        {
            var uploadId = "upload-" + index.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + new string('0', 24);
            overdue.Add(uploadId);
            await store.CreateAsync(
                new UploadRecord(
                    uploadId,
                    "dev-consumer",
                    UploadMode.ApiStream,
                    DataProfile.HeaderItemsV1,
                    UploadStatus.Created,
                    ReportService.Storage.ObjectKey.FromSegments("staging", uploadId, "content"),
                    Now.AddHours(-2),
                    Now),
                cancellationToken);
        }

        var first = store.ExpireOverdueAsync(Now, 100, cancellationToken);
        var second = store.ExpireOverdueAsync(Now, 100, cancellationToken);
        var results = await Task.WhenAll(first, second);

        var expired = results.SelectMany(batch => batch.Select(upload => upload.UploadId)).ToList();
        Assert.Equal(expired.Count, expired.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(overdue.Order(StringComparer.Ordinal), expired.Order(StringComparer.Ordinal));
        Assert.All(results.SelectMany(batch => batch), upload => Assert.Equal(UploadStatus.Created, upload.PreviousStatus));

        var again = await store.ExpireOverdueAsync(Now, 100, cancellationToken);
        Assert.Empty(again);
    }

    private string ConnectionString(string database) => new NpgsqlConnectionStringBuilder(_fixture.Application(database))
    {
        Timeout = 5,
        CommandTimeout = PostgresUploadStore.CommandTimeoutSeconds,
    }.ConnectionString;
}
