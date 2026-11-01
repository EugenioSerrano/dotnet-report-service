using System.Diagnostics.CodeAnalysis;
using Dapper;
using Npgsql;
using ReportService.Storage;

namespace ReportService.Uploads;

/// <summary>
/// The upload records in the application's own database, read and written with
/// explicit SQL over a pooled data source. Every statement is constant text with
/// parameters, so no value a caller sent ever becomes part of a statement; every
/// read a consumer drives carries the owner as a parameter of its own, so there
/// is no unscoped read to forget. Enumerations are written in their domain text
/// and read back strictly, a corrupted row being a fault rather than a default.
/// Each write is one conditional statement or one transaction, so a step that
/// arrives twice changes nothing. A database that cannot be reached or does not
/// answer in time is reported as <see cref="UploadStoreUnavailableException"/>,
/// whose message names no connection detail.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "PostgreSQL adapter; verified by the shared store contract against a real database in the integration cycle")]
public sealed class PostgresUploadStore : IUploadStore
{
    /// <summary>How long one command of this store may take.</summary>
    public const int CommandTimeoutSeconds = 5;

    private const string UniqueViolation = "23505";

    private const string InsertSql = """
        INSERT INTO report_service.uploads
            (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at)
        VALUES
            (@UploadId, @ConsumerId, @Mode, @Profile, @Status, @StagingKey, @CreatedAt, @ExpiresAt)
        """;

    private const string SelectScopedSql = """
        SELECT upload_id        AS "UploadId",
               consumer_id      AS "ConsumerId",
               mode             AS "Mode",
               declared_profile AS "Profile",
               status           AS "Status",
               staging_key      AS "StagingKey",
               created_at       AS "CreatedAt",
               expires_at       AS "ExpiresAt",
               content_encoding AS "ContentEncoding",
               content_outcome  AS "ContentOutcome",
               received_bytes   AS "ReceivedBytes",
               staging_size_bytes AS "StagingSize",
               staging_sha256   AS "StagingSha256",
               declared_sha256  AS "DeclaredSha256",
               completed_at     AS "CompletedAt",
               rejection_code   AS "RejectionCode",
               rejection_reason AS "RejectionReason"
        FROM report_service.uploads
        WHERE upload_id = @UploadId AND consumer_id = @ConsumerId
        """;

    private const string SelectForVerificationSql = """
        SELECT u.upload_id        AS "UploadId",
               u.consumer_id      AS "ConsumerId",
               u.mode             AS "Mode",
               u.declared_profile AS "Profile",
               u.status           AS "Status",
               u.staging_key      AS "StagingKey",
               u.created_at       AS "CreatedAt",
               u.expires_at       AS "ExpiresAt",
               u.content_encoding AS "ContentEncoding",
               u.content_outcome  AS "ContentOutcome",
               u.received_bytes   AS "ReceivedBytes",
               u.staging_size_bytes AS "StagingSize",
               u.staging_sha256   AS "StagingSha256",
               u.declared_sha256  AS "DeclaredSha256",
               u.completed_at     AS "CompletedAt",
               u.rejection_code   AS "RejectionCode",
               u.rejection_reason AS "RejectionReason",
               s.object_key          AS "SnapshotObjectKey",
               s.compression         AS "SnapshotCompression",
               s.profile             AS "SnapshotProfile",
               s.sha256_uncompressed AS "SnapshotSha256Uncompressed",
               s.size_uncompressed_bytes AS "SnapshotSizeUncompressed",
               s.sha256_stored       AS "SnapshotSha256Stored",
               s.size_stored_bytes   AS "SnapshotSizeStored",
               s.created_at          AS "SnapshotCreatedAt"
        FROM report_service.uploads u
        LEFT JOIN report_service.snapshots s ON s.upload_id = u.upload_id
        WHERE u.upload_id = @UploadId
        """;

    private const string StartTransferSql = """
        UPDATE report_service.uploads
           SET status = 'UPLOADING', content_encoding = @Encoding
         WHERE upload_id = @UploadId AND consumer_id = @ConsumerId AND status = 'CREATED'
        """;

    private const string RecordTransferSql = """
        UPDATE report_service.uploads
           SET content_outcome = @Outcome,
               received_bytes = @ReceivedBytes,
               staging_size_bytes = @StagingSize,
               staging_sha256 = @StagingSha256
         WHERE upload_id = @UploadId AND status = 'UPLOADING' AND content_outcome IS NULL
        """;

    private const string RejectTransferSql = """
        UPDATE report_service.uploads
           SET status = 'REJECTED',
               rejection_code = @Code,
               rejection_reason = @Reason,
               received_bytes = @ReceivedBytes
         WHERE upload_id = @UploadId AND status = 'UPLOADING' AND content_outcome IS NULL
        """;

    private const string ConfirmSql = """
        UPDATE report_service.uploads
           SET status = 'VERIFYING', completed_at = @CompletedAt, declared_sha256 = @DeclaredSha256
         WHERE upload_id = @UploadId AND consumer_id = @ConsumerId
           AND status = 'UPLOADING' AND content_outcome IS NOT NULL
        """;

    private const string InsertIntentSql = """
        INSERT INTO report_service.intents (kind, subject_id, status, attempts, created_at, next_attempt_at)
        VALUES ('start-upload-verification', @UploadId, 'PENDING', 0, @CompletedAt, @CompletedAt)
        ON CONFLICT (kind, subject_id) DO NOTHING
        """;

    private const string InsertSnapshotSql = """
        INSERT INTO report_service.snapshots
            (upload_id, consumer_id, object_key, compression, profile, sha256_uncompressed,
             size_uncompressed_bytes, sha256_stored, size_stored_bytes, created_at)
        VALUES
            (@UploadId, @ConsumerId, @ObjectKey, @Compression, @Profile, @Sha256Uncompressed,
             @SizeUncompressed, @Sha256Stored, @SizeStored, @CreatedAt)
        """;

    private const string MarkReadySql = """
        UPDATE report_service.uploads
           SET status = 'READY'
         WHERE upload_id = @UploadId AND status = 'VERIFYING'
        """;

    private const string CommitRejectedSql = """
        UPDATE report_service.uploads
           SET status = 'REJECTED', rejection_code = @Code, rejection_reason = @Reason
         WHERE upload_id = @UploadId AND status = 'VERIFYING'
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Reads and writes through <paramref name="dataSource"/>, which the process owns.</summary>
    /// <param name="dataSource">The pooled data source of the application's database.</param>
    public PostgresUploadStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task CreateAsync(UploadRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var parameters = new
        {
            record.UploadId,
            record.ConsumerId,
            Mode = UploadWireValues.ToStoredText(record.Mode),
            Profile = UploadWireValues.ToStoredText(record.Profile),
            Status = UploadWireValues.ToStoredText(record.Status),
            StagingKey = record.StagingKey.ToString(),
            CreatedAt = record.CreatedAt.ToUniversalTime().UtcDateTime,
            ExpiresAt = record.ExpiresAt.ToUniversalTime().UtcDateTime,
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                InsertSql,
                parameters,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);
        ArgumentNullException.ThrowIfNull(consumerId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<UploadRow>(new CommandDefinition(
                SelectScopedSql,
                new { UploadId = uploadId, ConsumerId = consumerId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row is null ? null : ToRecord(row);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryStartTransferAsync(
        string uploadId,
        string consumerId,
        UploadContentEncoding encoding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);
        ArgumentNullException.ThrowIfNull(consumerId);

        return await ExecuteAsync(
            StartTransferSql,
            new { UploadId = uploadId, ConsumerId = consumerId, Encoding = UploadWireValues.ToStoredText(encoding) },
            cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> RecordTransferAsync(
        string uploadId,
        UploadContentOutcome outcome,
        long receivedBytes,
        long? stagingSize,
        string? stagingSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);

        return await ExecuteAsync(
            RecordTransferSql,
            new
            {
                UploadId = uploadId,
                Outcome = UploadWireValues.ToStoredText(outcome),
                ReceivedBytes = receivedBytes,
                StagingSize = stagingSize,
                StagingSha256 = stagingSha256,
            },
            cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> RejectTransferAsync(
        string uploadId,
        UploadRejectionCode code,
        UploadRejectionReason reason,
        long receivedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);

        return await ExecuteAsync(
            RejectTransferSql,
            new
            {
                UploadId = uploadId,
                Code = UploadWireValues.ToStoredText(code),
                Reason = UploadWireValues.ToStoredText(reason),
                ReceivedBytes = receivedBytes,
            },
            cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(
        string uploadId,
        string consumerId,
        string? declaredSha256,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);
        ArgumentNullException.ThrowIfNull(consumerId);

        var parameters = new
        {
            UploadId = uploadId,
            ConsumerId = consumerId,
            DeclaredSha256 = declaredSha256,
            CompletedAt = completedAt.ToUniversalTime().UtcDateTime,
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                ConfirmSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (changed == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                InsertIntentSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<UploadRecord?> LoadForVerificationAsync(string uploadId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<UploadRow>(new CommandDefinition(
                SelectForVerificationSql,
                new { UploadId = uploadId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row is null ? null : ToRecord(row);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<SnapshotCommitResult> CommitReadyAsync(SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var parameters = new
        {
            snapshot.UploadId,
            snapshot.ConsumerId,
            ObjectKey = snapshot.ObjectKey.ToString(),
            Compression = UploadWireValues.ToStoredText(snapshot.Compression),
            Profile = UploadWireValues.ToStoredText(snapshot.Profile),
            snapshot.Sha256Uncompressed,
            snapshot.SizeUncompressed,
            snapshot.Sha256Stored,
            snapshot.SizeStored,
            CreatedAt = snapshot.CreatedAt.ToUniversalTime().UtcDateTime,
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                InsertSnapshotSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var changed = await connection.ExecuteAsync(new CommandDefinition(
                MarkReadySql,
                new { snapshot.UploadId },
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (changed == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return SnapshotCommitResult.NotVerifying;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return SnapshotCommitResult.Committed;
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            // The snapshot's primary key is the logical identity of the upload: the first insert wins.
            return SnapshotCommitResult.AlreadyCommitted;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CommitRejectedAsync(
        string uploadId,
        UploadRejectionCode code,
        UploadRejectionReason reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);

        return await ExecuteAsync(
            CommitRejectedSql,
            new
            {
                UploadId = uploadId,
                Code = UploadWireValues.ToStoredText(code),
                Reason = UploadWireValues.ToStoredText(reason),
            },
            cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<int> ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await connection.ExecuteAsync(new CommandDefinition(
                sql,
                parameters,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    private static UploadRecord ToRecord(UploadRow row) => new(
        row.UploadId,
        row.ConsumerId,
        UploadWireValues.ParseStoredMode(row.Mode),
        UploadWireValues.ParseStoredProfile(row.Profile),
        UploadWireValues.ParseStoredStatus(row.Status),
        ParseStagingKey(row.StagingKey),
        Utc(row.CreatedAt),
        Utc(row.ExpiresAt))
    {
        ContentEncoding = row.ContentEncoding is null ? null : UploadWireValues.ParseStoredEncoding(row.ContentEncoding),
        ContentOutcome = row.ContentOutcome is null ? null : UploadWireValues.ParseStoredOutcome(row.ContentOutcome),
        ReceivedBytes = row.ReceivedBytes,
        StagingSize = row.StagingSize,
        StagingSha256 = row.StagingSha256,
        DeclaredSha256 = row.DeclaredSha256,
        CompletedAt = row.CompletedAt is null ? null : Utc(row.CompletedAt.Value),
        RejectionCode = row.RejectionCode is null ? null : UploadWireValues.ParseStoredCode(row.RejectionCode),
        RejectionReason = row.RejectionReason is null ? null : UploadWireValues.ParseStoredReason(row.RejectionReason),
        Snapshot = row.SnapshotObjectKey is null
            ? null
            : new SnapshotRecord(
                row.UploadId,
                row.ConsumerId,
                ParseStagingKey(row.SnapshotObjectKey),
                UploadWireValues.ParseStoredEncoding(row.SnapshotCompression!),
                UploadWireValues.ParseStoredProfile(row.SnapshotProfile!),
                row.SnapshotSha256Uncompressed!,
                row.SnapshotSizeUncompressed!.Value,
                row.SnapshotSha256Stored!,
                row.SnapshotSizeStored!.Value,
                Utc(row.SnapshotCreatedAt!.Value)),
    };

    private static ObjectKey ParseStagingKey(string value) => ObjectKey.TryCreate(value, out var key, out var reason)
        ? key
        : throw new InvalidOperationException($"the stored object key is not a valid object key: {reason}");

    // The server returns timestamptz as a UTC DateTime; the record keeps the same instant with an offset of zero.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    // The server never answered, or it answered that it cannot serve right now: both are transient.
    private static bool IsUnavailable(Exception exception) => exception is TimeoutException
        || (exception is NpgsqlException sql && sql.IsTransient);

    private static UploadStoreUnavailableException Unavailable(Exception exception) => new(
        (exception as PostgresException ?? exception.InnerException as PostgresException)?.SqlState,
        exception);

    // Dapper maps the aliased columns onto these settable properties.
    private sealed class UploadRow
    {
        public string UploadId { get; set; } = string.Empty;

        public string ConsumerId { get; set; } = string.Empty;

        public string Mode { get; set; } = string.Empty;

        public string Profile { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string StagingKey { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public string? ContentEncoding { get; set; }

        public string? ContentOutcome { get; set; }

        public long? ReceivedBytes { get; set; }

        public long? StagingSize { get; set; }

        public string? StagingSha256 { get; set; }

        public string? DeclaredSha256 { get; set; }

        public DateTime? CompletedAt { get; set; }

        public string? RejectionCode { get; set; }

        public string? RejectionReason { get; set; }

        public string? SnapshotObjectKey { get; set; }

        public string? SnapshotCompression { get; set; }

        public string? SnapshotProfile { get; set; }

        public string? SnapshotSha256Uncompressed { get; set; }

        public long? SnapshotSizeUncompressed { get; set; }

        public string? SnapshotSha256Stored { get; set; }

        public long? SnapshotSizeStored { get; set; }

        public DateTime? SnapshotCreatedAt { get; set; }
    }
}
