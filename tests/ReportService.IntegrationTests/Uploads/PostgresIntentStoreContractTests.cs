extern alias orchestration;

using Dapper;
using Npgsql;
using ReportService.Migrations;
using ReportService.Testing;
using orchestration::ReportService.Workers.Orchestration.Dispatch;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// The durable-request store against a real PostgreSQL server: the same contract the
/// in-memory store is held to, run here on a database brought to the version the code
/// expects by the product's own migration set, plus what only a real schema can show —
/// two polls claiming disjoint sets of requests while the first holds its claim.
/// </summary>
public sealed class PostgresIntentStoreContractTests : IntentStoreContract, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private NpgsqlDataSource? _dataSource;
    private string? _database;
    private string? _connectionString;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PostgresIntentStoreContractTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Creates a database of its own, brings it to the expected version and opens the store on it.</summary>
    protected override async Task<IIntentStore> CreateStoreAsync(CancellationToken cancellationToken)
    {
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresIntentStore.CommandTimeoutSeconds,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));

        _dataSource = NpgsqlDataSource.Create(_connectionString);
        return new PostgresIntentStore(_dataSource);
    }

    /// <summary>Releases the data source of the class's store.</summary>
    protected override async ValueTask DisposeStoreAsync(IIntentStore store)
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    /// <summary>Records a pending request as the confirmation transaction would.</summary>
    protected override async Task<long> EnqueueAsync(
        string subjectId,
        DateTimeOffset due,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO report_service.intents (kind, subject_id, status, attempts, created_at, next_attempt_at)
            VALUES (@Kind, @SubjectId, 'PENDING', 0, @CreatedAt, @Due)
            RETURNING intent_id
            """,
            new { Kind, SubjectId = subjectId, CreatedAt = createdAt, Due = due },
            cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task TwoOpenPollsClaimDisjointSets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 4; index++)
        {
            await EnqueueAsync($"upload-{index:D3}00000000000000000000000000000", now, now, cancellationToken);
        }

        await using var first = await Store.ClaimDueAsync(now, batchSize: 2, cancellationToken);
        await using var second = await Store.ClaimDueAsync(now, batchSize: 2, cancellationToken);

        Assert.Equal(2, first.Intents.Count);
        Assert.Equal(2, second.Intents.Count);
        Assert.Empty(first.Intents.Select(intent => intent.IntentId).Intersect(second.Intents.Select(intent => intent.IntentId)));

        await first.CommitAsync(cancellationToken);
        await second.CommitAsync(cancellationToken);
    }

    [Fact]
    public async Task TheSchemaRefusesADispatchedRequestWithoutItsTime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.intents (kind, subject_id, status, attempts, created_at, next_attempt_at)
            VALUES ('start-upload-verification', 'upload-0123456789abcdef0123456789abcdef', 'DISPATCHED', 0, now(), now())
            """,
            cancellationToken: cancellationToken)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task OneRequestPerKindAndSubject()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero);
        await EnqueueAsync("upload-0123456789abcdef0123456789abcdef", now, now, cancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            EnqueueAsync("upload-0123456789abcdef0123456789abcdef", now, now, cancellationToken));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }
}
