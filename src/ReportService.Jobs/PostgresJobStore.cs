using System.Diagnostics.CodeAnalysis;
using Dapper;
using Npgsql;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.Jobs;

/// <summary>
/// The report job records in the application's own database, read and written with
/// explicit SQL over a pooled data source. Every statement is constant text with
/// parameters, so no value a caller sent ever becomes part of a statement.
/// Enumerations are written in their domain text and read back strictly, a
/// corrupted row being a fault rather than a default. Accepting a job is one
/// transaction that holds the upload it reports on, so the reference is usable at
/// the instant the job commits and is never usable and gone in the same breath. A
/// database that cannot be reached or does not answer in time is reported as
/// <see cref="JobStoreUnavailableException"/>, whose message names no connection
/// detail.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "PostgreSQL adapter; verified by the shared job-store contract against a real database in the integration cycle")]
public sealed partial class PostgresJobStore : IJobStore
{
    /// <summary>How long one command of this store may take.</summary>
    public const int CommandTimeoutSeconds = 5;

    /// <summary>The time bound of the acceptance transaction itself.</summary>
    public const string IdleTimeoutStatement = "SET LOCAL idle_in_transaction_session_timeout = '60s'";

    /// <summary>The longest the acceptance may wait for the upload's lock.</summary>
    public const string LockTimeoutStatement = "SET LOCAL lock_timeout = '5s'";

    private const string SelectUploadSql = """
        SELECT status                AS "Status",
               expires_at            AS "ExpiresAt",
               verification_deadline AS "VerificationDeadline"
        FROM report_service.uploads
        WHERE upload_id = @UploadId AND consumer_id = @ConsumerId
        FOR SHARE
        """;

    private const string InsertJobSql = """
        INSERT INTO report_service.report_jobs
            (report_job_id, consumer_id, upload_id, status, stage, report_clock, error_code,
             error_reason, created_at, started_at, finished_at)
        VALUES
            (@ReportJobId, @ConsumerId, @UploadId, 'QUEUED', NULL, @Now, NULL, NULL, @Now, NULL, NULL)
        """;

    private const string InsertOutputSql = """
        INSERT INTO report_service.report_outputs
            (output_id, report_job_id, format, status, error_code, started_at, finished_at)
        VALUES
            (@OutputId, @ReportJobId, 'PDF', 'PENDING', NULL, NULL, NULL)
        """;

    private const string InsertReferenceSql = """
        INSERT INTO report_service.template_references
            (report_job_id, template_id, reference_form, requested_version, baseline_version)
        VALUES
            (@ReportJobId, @TemplateId, @ReferenceForm, @RequestedVersion, @BaselineVersion)
        """;

    private const string InsertIntentSql = """
        INSERT INTO report_service.intents (kind, subject_id, status, attempts, created_at, next_attempt_at)
        VALUES ('start-job', @ReportJobId, 'PENDING', 0, @Now, @Now)
        """;

    private const string SelectJobSql = """
        SELECT j.report_job_id    AS "ReportJobId",
               j.consumer_id      AS "ConsumerId",
               j.upload_id        AS "UploadId",
               j.status           AS "Status",
               j.stage            AS "Stage",
               j.report_clock     AS "ReportClock",
               j.error_code       AS "ErrorCode",
               j.error_reason     AS "ErrorReason",
               j.error_message    AS "ErrorMessage",
               j.created_at       AS "CreatedAt",
               j.started_at       AS "StartedAt",
               j.finished_at      AS "FinishedAt",
               u.status           AS "UploadStatus",
               u.expires_at       AS "UploadExpiresAt",
               u.verification_deadline AS "VerificationDeadline",
               u.rejection_code   AS "RejectionCode",
               u.rejection_reason AS "RejectionReason"
        FROM report_service.report_jobs j
        JOIN report_service.uploads u ON u.upload_id = j.upload_id
        WHERE j.report_job_id = @ReportJobId
        """;

    private const string SelectOutputsSql = """
        SELECT output_id     AS "OutputId",
               report_job_id AS "ReportJobId",
               format        AS "Format",
               status        AS "Status",
               error_code    AS "ErrorCode",
               started_at    AS "StartedAt",
               finished_at   AS "FinishedAt"
        FROM report_service.report_outputs
        WHERE report_job_id = @ReportJobId
        ORDER BY output_id
        """;

    private const string SelectReferenceSql = """
        SELECT report_job_id     AS "ReportJobId",
               template_id       AS "TemplateId",
               reference_form    AS "ReferenceForm",
               requested_version AS "RequestedVersion",
               baseline_version  AS "BaselineVersion",
               resolved_version  AS "ResolvedVersion",
               commit_sha        AS "CommitSha",
               package_sha256    AS "PackageSha256",
               engine            AS "Engine",
               engine_version    AS "EngineVersion",
               schema_ref        AS "SchemaRef",
               resolved_at       AS "ResolvedAt"
        FROM report_service.template_references
        WHERE report_job_id = @ReportJobId
        """;

    private const string PinTemplateSql = """
        UPDATE report_service.template_references
           SET resolved_version = @Version,
               commit_sha       = @CommitSha,
               package_sha256   = @PackageSha256,
               engine           = @Engine,
               engine_version   = @EngineVersion,
               schema_ref       = @SchemaRef,
               resolved_at      = @ResolvedAt
         WHERE report_job_id = @ReportJobId
           AND resolved_at IS NULL
           AND EXISTS (SELECT 1 FROM report_service.report_jobs
                        WHERE report_job_id = @ReportJobId
                          AND status NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED'))
        """;

    private const string FailOutputsSql = """
        UPDATE report_service.report_outputs
           SET status = 'FAILED', error_code = @Code, finished_at = @Now
         WHERE report_job_id = @ReportJobId AND status IN ('PENDING', 'RUNNING')
        """;

    private const string FailJobSql = """
        UPDATE report_service.report_jobs
           SET status = 'FAILED', error_code = @Code, error_reason = @Reason,
               error_message = @Message, finished_at = @Now
         WHERE report_job_id = @ReportJobId AND status NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED')
        """;

    private const string BeginInputWaitSql = """
        UPDATE report_service.report_jobs
           SET stage = @Stage
         WHERE report_job_id = @ReportJobId AND status = 'QUEUED'
        RETURNING report_job_id
        """;

    private const string EndInputWaitSql = """
        UPDATE report_service.report_jobs
           SET stage = NULL
         WHERE report_job_id = @ReportJobId AND stage = @Stage
        RETURNING report_job_id
        """;

    private const string SelectInputWaitingJobsSql = """
        SELECT report_job_id
        FROM report_service.report_jobs
        WHERE upload_id = @UploadId
          AND status = 'QUEUED'
          AND (stage IS NULL OR stage = @Stage)
        ORDER BY created_at, report_job_id
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Reads and writes through <paramref name="dataSource"/>, which the process owns.</summary>
    /// <param name="dataSource">The pooled data source of the application's database.</param>
    public PostgresJobStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<JobAcceptanceResult> AcceptAsync(
        JobAcceptance acceptance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        var parameters = new
        {
            acceptance.ReportJobId,
            acceptance.OutputId,
            acceptance.ConsumerId,
            acceptance.UploadId,
            acceptance.TemplateId,
            ReferenceForm = JobWireValues.ToStoredText(acceptance.ReferenceForm),
            acceptance.RequestedVersion,
            acceptance.BaselineVersion,
            Now = UtcInstant(now),
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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

            // The upload is held from here to the commit, so nothing can refuse or expire it
            // between the moment it is found usable and the moment the job is durable.
            var upload = await connection.QuerySingleOrDefaultAsync<UploadStateRow>(new CommandDefinition(
                SelectUploadSql,
                new { acceptance.UploadId, acceptance.ConsumerId },
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (upload is null || !IsUsable(upload, now))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JobAcceptanceResult.UploadUnusable();
            }

            await connection.ExecuteAsync(new CommandDefinition(
                InsertJobSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                InsertOutputSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                InsertReferenceSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                InsertIntentSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // The job's clock and its creation are the instant the acceptance was decided at,
            // which is the value the caller holds and can compare the stored row with.
            var decidedAt = now.ToUniversalTime();
            return new JobAcceptanceResult.Accepted(new ReportJobRecord(
                acceptance.ReportJobId,
                acceptance.ConsumerId,
                acceptance.UploadId,
                ReportJobStatus.Queued,
                decidedAt,
                decidedAt));
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<JobStateRow>(new CommandDefinition(
                SelectJobSql,
                new { ReportJobId = reportJobId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            var outputs = await connection.QueryAsync<OutputRow>(new CommandDefinition(
                SelectOutputsSql,
                new { ReportJobId = reportJobId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return row.ToState([.. outputs.Select(output => output.ToRecord())]);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<ReferenceRow>(new CommandDefinition(
                SelectReferenceSql,
                new { ReportJobId = reportJobId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row?.ToRecord();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<TemplatePinState> PinTemplateAsync(
        string reportJobId,
        TemplatePin pin,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(pin);

        var parameters = new
        {
            ReportJobId = reportJobId,
            Version = pin.ResolvedVersion,
            pin.CommitSha,
            pin.PackageSha256,
            Engine = TemplateEngineNames.ToStoredText(pin.Engine),
            pin.EngineVersion,
            pin.SchemaRef,
            ResolvedAt = UtcInstant(now),
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var written = await connection.ExecuteAsync(new CommandDefinition(
                PinTemplateSql,
                parameters,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (written == 1)
            {
                return TemplatePinState.Pinned(pin);
            }

            // Nothing was written: an earlier attempt committed a pin first, or the job ended
            // before this one could. The record itself answers which, and never this attempt.
            var row = await connection.QuerySingleOrDefaultAsync<ReferenceRow>(new CommandDefinition(
                SelectReferenceSql,
                new { ReportJobId = reportJobId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row?.ToRecord().Pin is { } existing
                ? TemplatePinState.AlreadyPinned(existing)
                : TemplatePinState.JobEnded();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public Task<JobFailureOutcome> FailJobAsync(
        string reportJobId,
        string code,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        FailJobAsync(reportJobId, code, reason, null, now, cancellationToken);

    /// <inheritdoc />
    public async Task<JobFailureOutcome> FailJobAsync(
        string reportJobId,
        string code,
        string reason,
        string? message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(reason);

        var parameters = new
        {
            ReportJobId = reportJobId,
            Code = code,
            Reason = reason,
            Message = message,
            Now = UtcInstant(now),
        };
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // The outputs go first: a failure that reached the job without them would leave a
            // job that ended with an output still claiming to be in progress.
            await connection.ExecuteAsync(new CommandDefinition(
                FailOutputsSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            var failed = await connection.ExecuteAsync(new CommandDefinition(
                FailJobSql,
                parameters,
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return failed == 0 ? JobFailureOutcome.AlreadyTerminal : JobFailureOutcome.Applied;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var written = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                BeginInputWaitSql,
                new { ReportJobId = reportJobId, Stage = InputWaitStage.WaitingForInput },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return written is null ? BeginInputWaitOutcome.NotQueued : BeginInputWaitOutcome.Applied;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var written = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                EndInputWaitSql,
                new { ReportJobId = reportJobId, Stage = InputWaitStage.WaitingForInput },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return written is null ? EndInputWaitOutcome.NotWaiting : EndInputWaitOutcome.Applied;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploadId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var identifiers = await connection.QueryAsync<string>(new CommandDefinition(
                SelectInputWaitingJobsSql,
                new { UploadId = uploadId, Stage = InputWaitStage.WaitingForInput },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return [.. identifiers];
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    // The same rule the API answers with, stated once in the upload library: an upload that
    // was never confirmed must still be open, and a verifying one still inside its deadline.
    private static bool IsUsable(UploadStateRow upload, DateTimeOffset now)
    {
        var status = UploadWireValues.ParseStoredStatus(upload.Status);
        return status is UploadStatus.Ready or UploadStatus.Verifying
            && !UploadExpiry.IsOverdue(
                status,
                Utc(upload.ExpiresAt),
                upload.VerificationDeadline is { } deadline ? Utc(deadline) : null,
                now);
    }

    // The server returns timestamptz as a UTC DateTime; the record keeps the same instant with an offset of zero.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    // The stored engine text of a pin is read strictly: a value this build does not know is a fault.
    private static TemplateEngine StoredEngine(string? storedText) =>
        TemplateEngineNames.TryParseStoredText(storedText, out var engine)
            ? engine.Value
            : throw new InvalidOperationException("the stored template reference carries an engine this build does not know");

    // Every instant travels to the server as the UTCDateTime of the instant it names.
    private static DateTime UtcInstant(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime;

    // The server never answered, or it answered that it cannot serve right now: both are transient.
    private static bool IsUnavailable(Exception exception) => exception is TimeoutException
        || (exception is NpgsqlException sql && sql.IsTransient);

    private static JobStoreUnavailableException Unavailable(Exception exception) => new(
        (exception as PostgresException ?? exception.InnerException as PostgresException)?.SqlState,
        exception);

    // The upload row the acceptance holds, as the check needs it.
    private sealed class UploadStateRow
    {
        public string Status { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        public DateTime? VerificationDeadline { get; set; }
    }

    // Dapper maps the aliased columns onto these settable properties.
    private sealed class JobStateRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string ConsumerId { get; set; } = string.Empty;

        public string UploadId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? Stage { get; set; }

        public DateTime ReportClock { get; set; }

        public string? ErrorCode { get; set; }

        public string? ErrorReason { get; set; }

        public string? ErrorMessage { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? FinishedAt { get; set; }

        public string UploadStatus { get; set; } = string.Empty;

        public DateTime UploadExpiresAt { get; set; }

        public DateTime? VerificationDeadline { get; set; }

        public string? RejectionCode { get; set; }

        public string? RejectionReason { get; set; }

        public JobStateRecord ToState(IReadOnlyList<ReportOutputRecord> outputs) => new(
            new ReportJobRecord(
                ReportJobId,
                ConsumerId,
                UploadId,
                JobWireValues.ParseStoredStatus(Status),
                Utc(ReportClock),
                Utc(CreatedAt))
            {
                Stage = Stage,
                ErrorCode = ErrorCode,
                ErrorReason = ErrorReason,
                ErrorMessage = ErrorMessage,
                StartedAt = StartedAt is { } started ? Utc(started) : null,
                FinishedAt = FinishedAt is { } finished ? Utc(finished) : null,
            },
            outputs,
            UploadWireValues.ParseStoredStatus(UploadStatus),
            Utc(UploadExpiresAt),
            VerificationDeadline is { } deadline ? Utc(deadline) : null,
            RejectionCode is null ? null : UploadWireValues.ParseStoredCode(RejectionCode),
            RejectionReason is null ? null : UploadWireValues.ParseStoredReason(RejectionReason));
    }

    // The reference row: the form is kept in its domain text and read back strictly, and
    // the baseline and the pin are read as stored - the pin only when it is complete.
    private sealed class ReferenceRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string TemplateId { get; set; } = string.Empty;

        public string ReferenceForm { get; set; } = string.Empty;

        public string? RequestedVersion { get; set; }

        public string? BaselineVersion { get; set; }

        public string? ResolvedVersion { get; set; }

        public string? CommitSha { get; set; }

        public string? PackageSha256 { get; set; }

        public string? Engine { get; set; }

        public string? EngineVersion { get; set; }

        public string? SchemaRef { get; set; }

        public DateTime? ResolvedAt { get; set; }

        public TemplateReferenceRecord ToRecord() => new(
            ReportJobId,
            TemplateId,
            JobWireValues.ParseStoredForm(ReferenceForm))
        {
            RequestedVersion = RequestedVersion,
            BaselineVersion = BaselineVersion,
            Pin = ResolvedAt is { } resolvedAt && ResolvedVersion is not null
                ? new TemplatePin(
                    TemplateId,
                    ResolvedVersion,
                    CommitSha ?? string.Empty,
                    PackageSha256 ?? string.Empty,
                    StoredEngine(Engine),
                    EngineVersion ?? string.Empty,
                    SchemaRef ?? string.Empty,
                    Utc(resolvedAt))
                : null,
        };
    }

    private sealed class OutputRow
    {
        public string OutputId { get; set; } = string.Empty;

        public string ReportJobId { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? FinishedAt { get; set; }

        public ReportOutputRecord ToRecord() => new(
            OutputId,
            ReportJobId,
            JobWireValues.ParseStoredFormat(Format),
            JobWireValues.ParseStoredOutputStatus(Status))
        {
            ErrorCode = ErrorCode,
            StartedAt = StartedAt is { } started ? Utc(started) : null,
            FinishedAt = FinishedAt is { } finished ? Utc(finished) : null,
        };
    }
}
