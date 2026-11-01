using System.Diagnostics.CodeAnalysis;
using Dapper;
using Npgsql;
using ReportService.Storage;

namespace ReportService.Api.Uploads;

/// <summary>
/// The upload records in the application's own database, read and written with
/// explicit SQL over a pooled data source. Both statements are constant text with
/// parameters, so no value a caller sent ever becomes part of a statement; every
/// read carries the owner as a parameter of its own, so there is no unscoped
/// read to forget. Enumerations are written in their domain text and read back
/// strictly, a corrupted row being a fault rather than a default. A database that
/// cannot be reached or does not answer in time is reported as
/// <see cref="UploadStoreUnavailableException"/>, whose message names no
/// connection detail.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Runs only against a real PostgreSQL server; covered by the integration suite.")]
public sealed class PostgresUploadStore : IUploadStore
{
    /// <summary>How long one command of this store may take.</summary>
    public const int CommandTimeoutSeconds = 5;

    private const string InsertSql = """
        INSERT INTO report_service.uploads
            (upload_id, consumer_id, mode, declared_profile, status, staging_key, created_at, expires_at)
        VALUES
            (@UploadId, @ConsumerId, @Mode, @Profile, @Status, @StagingKey, @CreatedAt, @ExpiresAt)
        """;

    private const string SelectSql = """
        SELECT upload_id        AS "UploadId",
               consumer_id      AS "ConsumerId",
               mode             AS "Mode",
               declared_profile AS "Profile",
               status           AS "Status",
               staging_key      AS "StagingKey",
               created_at       AS "CreatedAt",
               expires_at       AS "ExpiresAt"
        FROM report_service.uploads
        WHERE upload_id = @UploadId AND consumer_id = @ConsumerId
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
                SelectSql,
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

    private static UploadRecord ToRecord(UploadRow row) => new(
        row.UploadId,
        row.ConsumerId,
        UploadWireValues.ParseStoredMode(row.Mode),
        UploadWireValues.ParseStoredProfile(row.Profile),
        UploadWireValues.ParseStoredStatus(row.Status),
        ParseStagingKey(row.StagingKey),
        Utc(row.CreatedAt),
        Utc(row.ExpiresAt));

    private static ObjectKey ParseStagingKey(string value) => ObjectKey.TryCreate(value, out var key, out var reason)
        ? key
        : throw new InvalidOperationException($"the stored staging key is not a valid object key: {reason}");

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
    }
}
