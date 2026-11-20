using System.Data;
using Dapper;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The job store's idempotency on a real PostgreSQL server: the checks and the unique index
/// that make one job per consumer and key, the guard that refuses a change of the members
/// that identify a request while a state transition still commits, and the race the design
/// rests on - two open transactions on one key, decided by the index before the upload is
/// looked at. Every case runs on a database of its own, brought to the expected version by
/// the product's own migration set, so what is asserted is the delivered schema and SQL.
/// </summary>
public sealed class PostgresJobStoreIdempotencyTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "dev-consumer";
    private const string Other = "other-consumer";

    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan WaitDeadline = TimeSpan.FromSeconds(15);

    private readonly PostgresFixture _fixture;
    private string _database = string.Empty;
    private string _connectionString = string.Empty;
    private NpgsqlDataSource _dataSource = null!;
    private PostgresJobStore _store = null!;
    private PostgresUploadStore _uploads = null!;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PostgresJobStoreIdempotencyTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));

        _dataSource = NpgsqlDataSource.Create(_connectionString);
        _store = new PostgresJobStore(_dataSource);
        _uploads = new PostgresUploadStore(_dataSource);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task TheSchemaRefusesAMalformedKeyOrIdentityAndAKeyWithoutItsIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        (string? Key, string? Fingerprint)[] refused =
        [
            ("has space", IdentityOf(uploadId, "1.0.0").Value),
            (new string('K', 256), IdentityOf(uploadId, "1.0.0").Value),
            ("K1", "v0:" + new string('a', 64)),
            ("K1", "v1:" + new string('A', 64)),
            ("K1", "v1:" + new string('a', 63)),
            ("K1", null),
        ];

        foreach (var (key, fingerprint) in refused)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO report_service.report_jobs
                    (report_job_id, consumer_id, upload_id, status, report_clock, created_at, idempotency_key, fingerprint)
                VALUES
                    (@ReportJobId, @ConsumerId, @UploadId, 'QUEUED', @Now, @Now, @Key, @Fingerprint)
                """,
                new
                {
                    ReportJobId = JobIdentifier.Create(),
                    ConsumerId = Consumer,
                    UploadId = uploadId,
                    Now,
                    Key = key,
                    Fingerprint = fingerprint,
                },
                cancellationToken: cancellationToken)));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
    }

    [Fact]
    public async Task TheUniqueIndexRefusesASecondJobOfOneConsumerWithTheSameKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(cancellationToken);
        var acceptance = Acceptance(uploadId, key: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await _store.AcceptAsync(acceptance, Now, cancellationToken));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // The same key of the same consumer: the index refuses the second row, and the same key
        // of another consumer is its own.
        var sameConsumer = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.report_jobs
                (report_job_id, consumer_id, upload_id, status, report_clock, created_at, idempotency_key, fingerprint)
            VALUES
                (@ReportJobId, @ConsumerId, @UploadId, 'QUEUED', @Now, @Now, 'K1', @Fingerprint)
            """,
            new
            {
                ReportJobId = JobIdentifier.Create(),
                ConsumerId = Consumer,
                UploadId = uploadId,
                Now,
                Fingerprint = acceptance.Fingerprint.Value,
            },
            cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, sameConsumer.SqlState);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.report_jobs
                (report_job_id, consumer_id, upload_id, status, report_clock, created_at, idempotency_key, fingerprint)
            VALUES
                (@ReportJobId, @ConsumerId, @UploadId, 'QUEUED', @Now, @Now, 'K1', @Fingerprint)
            """,
            new
            {
                ReportJobId = JobIdentifier.Create(),
                ConsumerId = Other,
                UploadId = uploadId,
                Now,
                Fingerprint = acceptance.Fingerprint.Value,
            },
            cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task TheGuardRefusesAChangeOfEachRequestMemberAndAllowsAStateTransition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(cancellationToken);
        var acceptance = Acceptance(uploadId, key: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await _store.AcceptAsync(acceptance, Now, cancellationToken));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        string[] refused =
        [
            "consumer_id = 'another-consumer'",
            $"upload_id = '{await ReadyUploadAsync(cancellationToken)}'",
            "idempotency_key = 'K2'",
            $"fingerprint = '{IdentityOf(uploadId, "1.0.0").Value}'",
        ];
        foreach (var assignment in refused)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(new CommandDefinition(
                $"UPDATE report_service.report_jobs SET {assignment} WHERE report_job_id = @ReportJobId",
                new { acceptance.ReportJobId },
                cancellationToken: cancellationToken)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        // The workflow's own transitions still write: the guard covers the request, not the state.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE report_service.report_jobs SET status = 'VALIDATING' WHERE report_job_id = @ReportJobId",
            new { acceptance.ReportJobId },
            cancellationToken: cancellationToken));
        var status = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM report_service.report_jobs WHERE report_job_id = @ReportJobId",
            new { acceptance.ReportJobId },
            cancellationToken: cancellationToken));
        Assert.Equal("VALIDATING", status);
    }

    [Fact]
    public async Task TwoOpenTransactionsOnOneKeyEndWithTheWinnersJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(cancellationToken);
        var fingerprint = IdentityOf(uploadId, "1.0.0");

        // The same request under the same key: the loser waits for the winner and receives its job.
        var winner = await HoldingInsertAsync(uploadId, "K1", fingerprint, cancellationToken);
        var loser = Acceptance(uploadId, key: "K1", fingerprint: fingerprint);
        var deciding = _store.AcceptAsync(loser, Now, cancellationToken);
        await WaitForLockAsync(cancellationToken);
        await winner.CommitAsync(cancellationToken);

        Assert.Equal(
            winner.JobId,
            Assert.IsType<JobAcceptanceResult.SameRequest>(
                await deciding.WaitAsync(WaitDeadline, cancellationToken)).ReportJobId);
        Assert.Null(await _store.FindForWorkflowAsync(loser.ReportJobId, cancellationToken));

        // A different request under the same key: the loser is refused, not given the job.
        var otherWinner = await HoldingInsertAsync(uploadId, "K2", fingerprint, cancellationToken);
        var conflicting = Acceptance(uploadId, key: "K2", fingerprint: IdentityOf(uploadId, "1.1.0"));
        var refused = _store.AcceptAsync(conflicting, Now, cancellationToken);
        await WaitForLockAsync(cancellationToken);
        await otherWinner.CommitAsync(cancellationToken);

        Assert.IsType<JobAcceptanceResult.DifferentRequest>(
            await refused.WaitAsync(WaitDeadline, cancellationToken));
        Assert.Null(await _store.FindForWorkflowAsync(conflicting.ReportJobId, cancellationToken));

        // A winner that rolls back leaves the key unbound: the loser becomes the first request.
        var rolledBack = await HoldingInsertAsync(uploadId, "K3", fingerprint, cancellationToken);
        var next = Acceptance(uploadId, key: "K3", fingerprint: fingerprint);
        var accepted = _store.AcceptAsync(next, Now, cancellationToken);
        await WaitForLockAsync(cancellationToken);
        await rolledBack.RollbackAsync(cancellationToken);

        Assert.Equal(
            next.ReportJobId,
            Assert.IsType<JobAcceptanceResult.Accepted>(
                await accepted.WaitAsync(WaitDeadline, cancellationToken)).Job.ReportJobId);
    }

    [Fact]
    public async Task ALoserIsAnsweredTheWinnersJobWhenTheUploadIsUnusableToIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(cancellationToken);
        var fingerprint = IdentityOf(uploadId, "1.0.0");

        // (a) The upload is overdue by the loser's later clock while the winner is open: the
        // loser waits on the key and receives the winner's job, never a missing upload.
        var winner = await HoldingInsertAsync(uploadId, "K1", fingerprint, cancellationToken);
        var loser = Acceptance(uploadId, key: "K1", fingerprint: fingerprint);
        var deciding = _store.AcceptAsync(loser, Now.AddHours(2), cancellationToken);
        await WaitForLockAsync(cancellationToken);
        await winner.CommitAsync(cancellationToken);

        Assert.Equal(
            winner.JobId,
            Assert.IsType<JobAcceptanceResult.SameRequest>(
                await deciding.WaitAsync(WaitDeadline, cancellationToken)).ReportJobId);

        // (c) The winner rolls back: the loser is checked on its own, and an overdue upload is
        // unusable to it, with no row left behind.
        var rolledBack = await HoldingInsertAsync(uploadId, "K2", fingerprint, cancellationToken);
        var overdue = Acceptance(uploadId, key: "K2", fingerprint: fingerprint);
        var checkedOnItsOwn = _store.AcceptAsync(overdue, Now.AddHours(2), cancellationToken);
        await WaitForLockAsync(cancellationToken);
        await rolledBack.RollbackAsync(cancellationToken);

        Assert.IsType<JobAcceptanceResult.UploadUnusable>(
            await checkedOnItsOwn.WaitAsync(WaitDeadline, cancellationToken));
        Assert.Null(await _store.FindForWorkflowAsync(overdue.ReportJobId, cancellationToken));
        Assert.IsType<IdempotencyLookup.None>(
            await _store.FindByIdempotencyKeyAsync(Consumer, "K2", fingerprint, cancellationToken));
    }

    [Fact]
    public async Task ALoserIsAnsweredTheWinnersJobWhenTheUploadIsRejectedWhileItWaits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadyUploadAsync(verifying: true, cancellationToken);
        var fingerprint = IdentityOf(uploadId, "1.0.0");

        // (b) A third transaction refuses the upload and waits for the winner's own lock; the
        // loser waits on the key. The winner commits, the refusal commits, and the loser is
        // still answered the winner's job.
        var winner = await HoldingInsertAsync(uploadId, "K1", fingerprint, cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var refusal = await connection.BeginTransactionAsync(cancellationToken);
        var rejecting = connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE report_service.uploads
               SET status = 'REJECTED', rejection_code = 'DATA_INVALID', rejection_reason = 'truncated'
             WHERE upload_id = @UploadId
            """,
            new { UploadId = uploadId },
            refusal,
            cancellationToken: cancellationToken));

        var loser = Acceptance(uploadId, key: "K1", fingerprint: fingerprint);
        var deciding = _store.AcceptAsync(loser, Now, cancellationToken);
        await WaitForLockAsync(cancellationToken);
        await winner.CommitAsync(cancellationToken);
        await rejecting.WaitAsync(WaitDeadline, cancellationToken);
        await refusal.CommitAsync(cancellationToken);

        Assert.Equal(
            winner.JobId,
            Assert.IsType<JobAcceptanceResult.SameRequest>(
                await deciding.WaitAsync(WaitDeadline, cancellationToken)).ReportJobId);
    }

    [Fact]
    public async Task TheAcceptanceTransactionRunsAtReadCommitted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The level the store begins its acceptance at, read back from the server inside a
        // transaction begun the same way: Read Committed is the only level where a loser sees
        // the winner's committed row instead of failing on a serialization conflict.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var level = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SHOW transaction_isolation",
            transaction: transaction,
            cancellationToken: cancellationToken));

        Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
        Assert.Equal("read committed", level);
    }

    // One open transaction that inserts the job row with the key and holds it, the way a
    // concurrent acceptance holds it between its insert and its commit.
    private async Task<HeldInsert> HoldingInsertAsync(
        string uploadId,
        string key,
        RequestFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var jobId = JobIdentifier.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO report_service.report_jobs
                (report_job_id, consumer_id, upload_id, status, report_clock, created_at, idempotency_key, fingerprint)
            VALUES
                (@ReportJobId, @ConsumerId, @UploadId, 'QUEUED', @Now, @Now, @Key, @Fingerprint)
            """,
            new { ReportJobId = jobId, ConsumerId = Consumer, UploadId = uploadId, Now, Key = key, Fingerprint = fingerprint.Value },
            transaction,
            cancellationToken: cancellationToken));
        return new HeldInsert(jobId, connection, transaction);
    }

    private async Task WaitForLockAsync(CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            "a transaction waiting for the key's row",
            async token =>
            {
                await using var probe = await _fixture.OpenAsync(_database, token);
                var waiting = await probe.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()",
                    cancellationToken: token));
                return waiting > 0;
            },
            WaitDeadline,
            TimeSpan.FromMilliseconds(50),
            cancellationToken);

    private JobAcceptance Acceptance(
        string uploadId,
        string? key = null,
        RequestFingerprint? fingerprint = null,
        string version = "1.0.0") =>
        new(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            version,
            fingerprint ?? IdentityOf(uploadId, version))
        {
            IdempotencyKey = key,
        };

    private static RequestFingerprint IdentityOf(string uploadId, string version) =>
        AcceptanceIdentity.For(uploadId, version);

    private async Task<string> ReadyUploadAsync(CancellationToken cancellationToken) =>
        await ReadyUploadAsync(verifying: false, cancellationToken);

    private async Task<string> ReadyUploadAsync(bool verifying, CancellationToken cancellationToken)
    {
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await _uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                Now.AddHours(-1),
                Now.AddHours(1)),
            cancellationToken);
        Assert.True(await _uploads.TryStartTransferAsync(
            uploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken));
        Assert.True(await _uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), Now.AddMinutes(-20), cancellationToken));
        Assert.True(await _uploads.ConfirmAsync(
            uploadId, Consumer, null, Now.AddMinutes(-21), Now.AddMinutes(-20), Now.AddHours(5), cancellationToken));
        if (!verifying)
        {
            Assert.Equal(
                SnapshotCommitResult.Committed,
                await _uploads.CommitReadyAsync(
                    new SnapshotRecord(
                        uploadId,
                        Consumer,
                        ObjectKey.FromSegments("snapshots", uploadId, "a"),
                        UploadContentEncoding.None,
                        DataProfile.HeaderItemsV1,
                        new string('a', 64),
                        900,
                        new string('a', 64),
                        900,
                        Now.AddMinutes(-10)),
                    Now.AddMinutes(-10),
                    cancellationToken));
        }

        return uploadId;
    }

    // The winner's open transaction and the row it wrote, held until the case ends it.
    private sealed class HeldInsert(string jobId, NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IAsyncDisposable
    {
        public string JobId { get; } = jobId;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            await DisposeAsync();
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            await transaction.RollbackAsync(cancellationToken);
            await DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

}
