using Dapper;
using Npgsql;
using ReportService.Api.Uploads;
using ReportService.Migrations;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// The upload store against a real PostgreSQL server: the same contract the
/// in-memory store is held to, run here on a database brought to the version the
/// code expects by the product's own migration set, plus what only a real schema
/// can show — the constraints that refuse a bad row and the unique key that
/// refuses a repeated identifier.
/// </summary>
public sealed class PostgresUploadStoreContractTests : UploadStoreContract, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource? _dataSource;
    private string? _database;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PostgresUploadStoreContractTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Creates a database of its own, brings it to the expected version and opens the store on it.</summary>
    protected override async Task<IUploadStore> CreateStoreAsync(CancellationToken cancellationToken)
    {
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = ConnectionString(_database);

        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));

        _dataSource = NpgsqlDataSource.Create(connectionString);
        return new PostgresUploadStore(_dataSource);
    }

    /// <summary>Releases the data source of the class's store.</summary>
    protected override async ValueTask DisposeStoreAsync(IUploadStore store)
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    // The connection the store and the raw statements use: bounded, so a server that stops answering is reported.
    private string ConnectionString(string database) => new NpgsqlConnectionStringBuilder(_fixture.Application(database))
    {
        Timeout = 5,
        CommandTimeout = PostgresUploadStore.CommandTimeoutSeconds,
    }.ConnectionString;

    [Theory]
    [InlineData("abcdef", "CREATED", 0)]
    [InlineData("upload-0123456789ABCDEF0123456789ABCDEF", "CREATED", 0)]
    [InlineData("upload-0123456789abcdef0123456789abcdef", "PENDING", 0)]
    [InlineData("upload-0123456789abcdef0123456789abcdef", "CREATED", -1)]
    [InlineData("upload-0123456789abcdef0123456789abcdef", "CREATED", 0)]
    public async Task TheSchemaRefusesARowTheStoreWouldNeverWrite(string uploadId, string status, int expiryOffsetSeconds)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.OpenAsync(_database!, cancellationToken);
        var created = new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.uploads
                (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at)
            VALUES
                (@uploadId, 'dev-consumer', 'API_STREAM', 'HEADER_ITEMS_V1', @status, @stagingKey, @createdAt, @expiresAt)
            """,
            new
            {
                uploadId,
                status,
                stagingKey = "staging/probe/content",
                createdAt = created.UtcDateTime,
                expiresAt = created.AddSeconds(expiryOffsetSeconds).UtcDateTime,
            },
            cancellationToken: cancellationToken)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task ARepeatedIdentifierIsRefusedByTheUniqueKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var record = Session("upload-55555555555555555555555555555555");

        await Store.CreateAsync(record, cancellationToken);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Store.CreateAsync(record, cancellationToken));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }
}
