using System.Diagnostics.CodeAnalysis;
using Dapper;
using Npgsql;

namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>
/// The durable requests in the application's own database. One poll opens one
/// transaction, claims a bounded batch of the requests that are due while skipping
/// the rows another process holds, and returns them with the transaction still
/// open, so what the dispatcher decides is written by the same act that held the
/// rows. Every statement is constant text with parameters; the transaction is
/// bounded in time so a slow engine call can never hold a row for longer than the
/// deployment allows.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "PostgreSQL adapter; verified by the shared intent-store contract against a real database in the integration cycle")]
public sealed class PostgresIntentStore(NpgsqlDataSource dataSource) : IIntentStore
{
    /// <summary>How long one command of this store may take.</summary>
    public const int CommandTimeoutSeconds = 5;

    /// <summary>The time bound of the claim transaction itself.</summary>
    public const string IdleTimeoutStatement = "SET LOCAL idle_in_transaction_session_timeout = '60s'";

    /// <summary>The longest a lock of the claim may be held while it waits.</summary>
    public const string LockTimeoutStatement = "SET LOCAL lock_timeout = '5s'";

    private const string ClaimSql = """
        SELECT intent_id  AS "IntentId",
               kind       AS "Kind",
               subject_id AS "SubjectId",
               attempts   AS "Attempts",
               created_at AS "CreatedAt"
        FROM report_service.intents
        WHERE status = 'PENDING' AND next_attempt_at <= @Now
        ORDER BY intent_id
        LIMIT @BatchSize
        FOR UPDATE SKIP LOCKED
        """;

    private const string MarkDispatchedSql = """
        UPDATE report_service.intents
        SET status = 'DISPATCHED', dispatched_at = @Now, last_attempt_at = @Now, attempts = attempts + 1
        WHERE intent_id = @IntentId AND status = 'PENDING'
        """;

    private const string InsertReferenceSql = """
        INSERT INTO report_service.execution_references
            (intent_id, workflow_id, run_id, worker_build, retry_policy, recorded_at)
        VALUES
            (@IntentId, @WorkflowId, @RunId, @WorkerBuild, @RetryPolicy, @RecordedAt)
        """;

    private const string RecordFailureSql = """
        UPDATE report_service.intents
        SET attempts = attempts + 1, last_attempt_at = @Now, next_attempt_at = @NextAttemptAt
        WHERE intent_id = @IntentId AND status = 'PENDING'
        """;

    private const string OldestPendingSql = """
        SELECT min(created_at) FROM report_service.intents WHERE status = 'PENDING'
        """;

    /// <inheritdoc />
    public async Task<IIntentClaim> ClaimDueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                IdleTimeoutStatement,
                transaction: transaction,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                LockTimeoutStatement,
                transaction: transaction,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            var claimed = await connection.QueryAsync<IntentRow>(new CommandDefinition(
                ClaimSql,
                new { Now = now, BatchSize = batchSize },
                transaction,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new Claim(connection, transaction, [.. claimed.Select(row => row.ToRecord())]);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<TimeSpan?> OldestPendingAgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var oldest = await connection.QuerySingleAsync<DateTime?>(new CommandDefinition(
            OldestPendingSql,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return oldest is { } instant ? now - Utc(instant) : null;
    }

    // The server returns a timestamptz as a UTC DateTime; the record keeps the same instant
    // with an offset of zero, so a caller compares instants and never a wall-clock time.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>One claimed request as the database returns it.</summary>
    private sealed record IntentRow(long IntentId, string Kind, string SubjectId, int Attempts, DateTime CreatedAt)
    {
        public IntentRecord ToRecord() => new(IntentId, Kind, SubjectId, Attempts, Utc(CreatedAt));
    }

    private sealed class Claim(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<IntentRecord> intents) : IIntentClaim
    {
        private bool _committed;

        public IReadOnlyList<IntentRecord> Intents => intents;

        public async Task MarkDispatchedAsync(long intentId, ExecutionReference reference, DateTimeOffset now, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reference);
            await connection.ExecuteAsync(new CommandDefinition(
                MarkDispatchedSql,
                new { IntentId = intentId, Now = now },
                transaction,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                InsertReferenceSql,
                new
                {
                    IntentId = intentId,
                    reference.WorkflowId,
                    reference.RunId,
                    reference.WorkerBuild,
                    reference.RetryPolicy,
                    reference.RecordedAt,
                },
                transaction,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        public Task RecordFailureAsync(long intentId, DateTimeOffset now, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken) =>
            connection.ExecuteAsync(new CommandDefinition(
                RecordFailureSql,
                new { IntentId = intentId, Now = now, NextAttemptAt = nextAttemptAt },
                transaction,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken));

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
