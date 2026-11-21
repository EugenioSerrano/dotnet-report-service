using Dapper;
using Npgsql;

namespace ReportService.Jobs;

/// <summary>
/// The partition, block-commit, publication and finalisation records of a job, in the
/// application's own database. Every write is one transaction over constant SQL with
/// parameters, and the two commits that must not follow a refused step hold the guard
/// first: the job is not ending or ended, and the output is still running. A commit
/// that another attempt wrote first is reported with the record it wrote, so a step
/// that arrives twice changes nothing and learns what the first one recorded.
/// </summary>
public sealed partial class PostgresJobStore
{
    private const string SelectJobStatusSql = """
        SELECT status AS "Status" FROM report_service.report_jobs WHERE report_job_id = @ReportJobId
        """;

    private const string SelectOutputStatusSql = """
        SELECT status AS "Status" FROM report_service.report_outputs WHERE output_id = @OutputId
        """;

    private const string TransitionJobSql = """
        UPDATE report_service.report_jobs
           SET status = @To, stage = @Stage, started_at = COALESCE(started_at, @Now)
         WHERE report_job_id = @ReportJobId AND status = ANY(@From)
        RETURNING created_at
        """;

    private const string TransitionOutputSql = """
        UPDATE report_service.report_outputs
           SET status = 'RUNNING', started_at = @Now
         WHERE output_id = @OutputId AND status = 'PENDING'
        """;

    private const string InsertPartitionSql = """
        INSERT INTO report_service.partitions
            (report_job_id, partitioner_version, block_count, record_count, record_bytes,
             index_object_key, index_sha256, index_size_bytes, committed_at,
             pdf_refusal_code, pdf_refusal_reason)
        VALUES
            (@ReportJobId, @PartitionerVersion, @BlockCount, @RecordCount, @RecordBytes,
             @IndexObjectKey, @IndexSha256, @IndexSizeBytes, @CommittedAt,
             @PdfRefusalCode, @PdfRefusalReason)
        ON CONFLICT (report_job_id) DO NOTHING
        """;

    private const string SelectPartitionSql = """
        SELECT report_job_id       AS "ReportJobId",
               partitioner_version AS "PartitionerVersion",
               block_count         AS "BlockCount",
               record_count        AS "RecordCount",
               record_bytes        AS "RecordBytes",
               index_object_key    AS "IndexObjectKey",
               index_sha256        AS "IndexSha256",
               index_size_bytes    AS "IndexSizeBytes",
               committed_at        AS "CommittedAt",
               pdf_refusal_code    AS "PdfRefusalCode",
               pdf_refusal_reason  AS "PdfRefusalReason"
        FROM report_service.partitions
        WHERE report_job_id = @ReportJobId
        """;

    private const string GuardSql = """
        SELECT j.report_job_id AS "ReportJobId", j.status AS "JobStatus", o.status AS "OutputStatus"
        FROM report_service.report_outputs o
        JOIN report_service.report_jobs j ON j.report_job_id = o.report_job_id
        WHERE o.output_id = @OutputId
        FOR UPDATE OF o
        """;

    private const string InsertPartCommitSql = """
        INSERT INTO report_service.pdf_part_commits
            (output_id, block_index, object_key, sha256, size_bytes, page_count,
             template_package_sha256, committed_at, presentation)
        VALUES
            (@OutputId, @BlockIndex, @ObjectKey, @Sha256, @SizeBytes, @PageCount,
             @TemplatePackageSha256, @CommittedAt, @Presentation::jsonb)
        ON CONFLICT (output_id, block_index) DO NOTHING
        """;

    private const string SelectPartCommitSql = """
        SELECT output_id               AS "OutputId",
               block_index             AS "BlockIndex",
               object_key              AS "ObjectKey",
               sha256                  AS "Sha256",
               size_bytes              AS "SizeBytes",
               page_count              AS "PageCount",
               template_package_sha256 AS "TemplatePackageSha256",
               committed_at            AS "CommittedAt",
               presentation::text      AS "Presentation"
        FROM report_service.pdf_part_commits
        WHERE output_id = @OutputId AND block_index = @BlockIndex
        """;

    private const string InsertArtifactSql = """
        INSERT INTO report_service.artifacts
            (artifact_id, report_job_id, output_id, format, media_type, object_key, sha256,
             size_bytes, page_count, created_at, expires_at)
        VALUES
            (@ArtifactId, @ReportJobId, @OutputId, @Format, @MediaType, @ObjectKey, @Sha256,
             @SizeBytes, @PageCount, @CreatedAt, @ExpiresAt)
        ON CONFLICT (output_id) DO NOTHING
        """;

    private const string ListArtifactsSql = """
        SELECT j.report_job_id AS "ReportJobId", a.artifact_id AS "ArtifactId", a.format AS "Format",
               a.media_type AS "MediaType", a.object_key AS "ObjectKey", a.sha256 AS "Sha256",
               a.size_bytes AS "SizeBytes", a.created_at AS "CreatedAt", a.expires_at AS "ExpiresAt"
          FROM report_service.report_jobs j
          LEFT JOIN report_service.artifacts a ON a.report_job_id = j.report_job_id
         WHERE j.report_job_id = @ReportJobId AND j.consumer_id = @ConsumerId
         ORDER BY array_position(ARRAY['PDF','HTML','XLSX','MARKDOWN'], a.format), a.artifact_id
        """;

    private const string FindArtifactSql = """
        SELECT a.report_job_id AS "ReportJobId", a.artifact_id AS "ArtifactId", a.format AS "Format",
               a.media_type AS "MediaType", a.object_key AS "ObjectKey", a.sha256 AS "Sha256",
               a.size_bytes AS "SizeBytes", a.created_at AS "CreatedAt", a.expires_at AS "ExpiresAt"
          FROM report_service.artifacts a
          JOIN report_service.report_jobs j ON j.report_job_id = a.report_job_id
         WHERE a.artifact_id = @ArtifactId AND a.report_job_id = @ReportJobId AND j.consumer_id = @ConsumerId
        """;

    private const string SelectArtifactIdSql = """
        SELECT artifact_id AS "ArtifactId" FROM report_service.artifacts WHERE output_id = @OutputId
        """;

    private const string MarkOutputSucceededSql = """
        UPDATE report_service.report_outputs
           SET status = 'SUCCEEDED', finished_at = @Now
         WHERE output_id = @OutputId AND status = 'RUNNING'
        """;

    private const string FailOneOutputSql = """
        UPDATE report_service.report_outputs
           SET status = 'FAILED', error_code = @Code, error_reason = @Reason, finished_at = @Now
         WHERE output_id = @OutputId AND status IN ('PENDING', 'RUNNING')
        """;

    private const string SelectOutputsForFinalizationSql = """
        SELECT output_id AS "OutputId", status AS "Status", error_code AS "ErrorCode",
               error_reason AS "ErrorReason"
        FROM report_service.report_outputs
        WHERE report_job_id = @ReportJobId
        ORDER BY output_id
        """;

    private const string FinalizeJobSql = """
        UPDATE report_service.report_jobs
           SET status = @Status, error_code = @Code, error_reason = @Reason, finished_at = @Now
         WHERE report_job_id = @ReportJobId
           AND status NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED')
        """;

    private const string InsertAttemptCauseSql = """
        INSERT INTO report_service.pdf_attempt_causes
            (output_id, block_index, attempt, cause, recorded_at)
        VALUES
            (@OutputId, @BlockIndex, @Attempt, @Cause, @RecordedAt)
        ON CONFLICT (output_id, block_index, attempt) DO NOTHING
        """;

    private const string SelectLastAttemptCauseSql = """
        SELECT output_id   AS "OutputId",
               block_index AS "BlockIndex",
               attempt     AS "Attempt",
               cause       AS "Cause",
               recorded_at AS "RecordedAt"
        FROM report_service.pdf_attempt_causes
        WHERE output_id = @OutputId AND block_index = @BlockIndex
        ORDER BY attempt DESC
        LIMIT 1
        """;

    private const string RaisePartitionProgressSql = """
        UPDATE report_service.report_jobs
           SET records_read  = GREATEST(records_read, @RecordCount),
               records_total = COALESCE(records_total, @RecordCount),
               blocks_total  = COALESCE(blocks_total, @BlockCount)
         WHERE report_job_id = @ReportJobId
           AND status NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED')
        """;

    private const string RaisePartProgressSql = """
        UPDATE report_service.report_jobs j
           SET blocks_completed = GREATEST(j.blocks_completed, c.n),
               pages_generated  = GREATEST(j.pages_generated, c.p)
          FROM (SELECT count(*)::int AS n, coalesce(sum(pc.page_count), 0)::bigint AS p
                  FROM report_service.pdf_part_commits pc
                  JOIN report_service.report_outputs o ON o.output_id = pc.output_id
                 WHERE o.report_job_id = @ReportJobId AND o.format = 'PDF') c
         WHERE j.report_job_id = @ReportJobId
           AND j.status NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED')
        """;

    /// <inheritdoc />
    public async Task<TransitionJobResult> TransitionJobAsync(
        string reportJobId,
        IReadOnlyList<ReportJobStatus> from,
        ReportJobStatus to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(from);

        // The stage is decided before the store is reached: a target no step of this
        // increment enters is a programming error, never a default stage.
        var parameters = new
        {
            ReportJobId = reportJobId,
            From = from.Select(JobWireValues.ToStoredText).ToArray(),
            To = JobWireValues.ToStoredText(to),
            Stage = JobWireValues.ToStoredText(JobStages.StageFor(to)),
            Now = UtcInstant(now),
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var createdAt = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                TransitionJobSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (createdAt is { } accepted)
            {
                return new TransitionJobResult(TransitionOutcome.Applied, Utc(accepted));
            }

            var current = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                SelectJobStatusSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return current is not null && JobWireValues.ParseStoredStatus(current) == to
                ? new TransitionJobResult(TransitionOutcome.AlreadyApplied, null)
                : new TransitionJobResult(TransitionOutcome.NotApplicable, null);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<TransitionOutcome> TransitionOutputAsync(
        string outputId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputId);
        var parameters = new { OutputId = outputId, Now = UtcInstant(now) };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                TransitionOutputSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (changed > 0)
            {
                return TransitionOutcome.Applied;
            }

            var current = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                SelectOutputStatusSql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return current is not null && JobWireValues.ParseStoredOutputStatus(current) == OutputStatus.Running
                ? TransitionOutcome.AlreadyApplied
                : TransitionOutcome.NotApplicable;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<PartitionCommitResult> CommitPartitionAsync(
        PartitionRecord partition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var parameters = new
        {
            partition.ReportJobId,
            partition.PartitionerVersion,
            partition.BlockCount,
            partition.RecordCount,
            partition.RecordBytes,
            partition.IndexObjectKey,
            partition.IndexSha256,
            partition.IndexSizeBytes,
            CommittedAt = UtcInstant(partition.CommittedAt),
            partition.PdfRefusalCode,
            partition.PdfRefusalReason,
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var written = await connection.ExecuteAsync(new CommandDefinition(
                InsertPartitionSql, parameters, transaction, CommandTimeoutSeconds, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (written > 0)
            {
                // The totals are set once, inside the same transaction that made the
                // partition durable, and the read count rises to the count that was counted.
                await connection.ExecuteAsync(new CommandDefinition(
                    RaisePartitionProgressSql,
                    new { partition.ReportJobId, partition.RecordCount, partition.BlockCount },
                    transaction,
                    CommandTimeoutSeconds,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var row = await connection.QuerySingleOrDefaultAsync<PartitionRow>(new CommandDefinition(
                SelectPartitionSql,
                new { partition.ReportJobId },
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var existing = row?.ToRecord() ?? partition;
            return new PartitionCommitResult(
                written > 0 ? PartitionCommitOutcome.Committed : PartitionCommitOutcome.AlreadyCommitted,
                existing);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<PartitionRow>(new CommandDefinition(
                SelectPartitionSql,
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
    public async Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var parameters = new
        {
            commit.OutputId,
            commit.BlockIndex,
            commit.ObjectKey,
            commit.Sha256,
            commit.SizeBytes,
            commit.PageCount,
            commit.TemplatePackageSha256,
            CommittedAt = UtcInstant(commit.CommittedAt),
            Presentation = commit.Presentation?.ToJson() ?? PresentationProvenance.UnrecordedJson,
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var guard = await GuardAsync(connection, transaction, commit.OutputId, cancellationToken).ConfigureAwait(false);
            if (guard is null || !AllowsCommit(guard))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PartCommitResult(PartCommitOutcome.GuardRefused, null);
            }

            var written = await connection.ExecuteAsync(new CommandDefinition(
                InsertPartCommitSql, parameters, transaction, CommandTimeoutSeconds, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (written > 0)
            {
                // The counters are recomputed from the insert-once commits and applied with
                // GREATEST: a repeated or racing commit cannot count a block or a page twice.
                await connection.ExecuteAsync(new CommandDefinition(
                    RaisePartProgressSql,
                    new { guard.ReportJobId },
                    transaction,
                    CommandTimeoutSeconds,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var row = await connection.QuerySingleOrDefaultAsync<PartCommitRow>(new CommandDefinition(
                SelectPartCommitSql,
                new { commit.OutputId, commit.BlockIndex },
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return written > 0
                ? new PartCommitResult(PartCommitOutcome.Committed, row?.ToRecord() ?? commit)
                : new PartCommitResult(PartCommitOutcome.AlreadyCommitted, row?.ToRecord());
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<PartCommitRow>(new CommandDefinition(
                SelectPartCommitSql,
                new { OutputId = outputId, BlockIndex = blockIndex },
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
    public async Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var parameters = new
        {
            artifact.ArtifactId,
            artifact.ReportJobId,
            artifact.OutputId,
            Format = JobWireValues.ToStoredText(artifact.Format),
            artifact.MediaType,
            artifact.ObjectKey,
            artifact.Sha256,
            artifact.SizeBytes,
            artifact.PageCount,
            CreatedAt = UtcInstant(artifact.CreatedAt),
            ExpiresAt = UtcInstant(artifact.ExpiresAt),
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // An output that was published already answers with what exists, whatever state it
            // reached since: the answer names the artifact, and nothing is written.
            var existing = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                SelectArtifactIdSql,
                new { artifact.OutputId },
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PublishResult(PublishOutcome.AlreadyPublished, existing);
            }

            var guard = await GuardAsync(connection, transaction, artifact.OutputId, cancellationToken).ConfigureAwait(false);
            if (guard is null || !AllowsCommit(guard))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new PublishResult(PublishOutcome.GuardRefused, null);
            }

            var written = await connection.ExecuteAsync(new CommandDefinition(
                InsertArtifactSql, parameters, transaction, CommandTimeoutSeconds, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (written > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    MarkOutputSucceededSql,
                    new { artifact.OutputId, Now = UtcInstant(artifact.CreatedAt) },
                    transaction,
                    CommandTimeoutSeconds,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return written > 0
                ? new PublishResult(PublishOutcome.Published, existing ?? artifact.ArtifactId)
                : new PublishResult(PublishOutcome.AlreadyPublished, existing);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<JobArtifactsView?> ListArtifactsAsync(
        string reportJobId,
        string consumerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(consumerId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var rows = (await connection.QueryAsync<ArtifactRow>(new CommandDefinition(
                ListArtifactsSql,
                new { ReportJobId = reportJobId, ConsumerId = consumerId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            if (rows.Count == 0)
            {
                return null;
            }

            // A job with no published artifact answers one row whose artifact columns are all
            // null: its list is empty, and the job itself is still the one the caller owns.
            return new JobArtifactsView(
                rows[0].ReportJobId,
                [.. rows.Where(row => row.ArtifactId is not null).Select(row => row.ToView())]);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<ArtifactView?> FindArtifactAsync(
        string reportJobId,
        string artifactId,
        string consumerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(consumerId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<ArtifactRow>(new CommandDefinition(
                FindArtifactSql,
                new { ReportJobId = reportJobId, ArtifactId = artifactId, ConsumerId = consumerId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row?.ToView();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<OutputFailureOutcome> FailOutputAsync(
        string outputId,
        string code,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputId);
        ArgumentNullException.ThrowIfNull(code);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                FailOneOutputSql,
                new { OutputId = outputId, Code = code, Reason = reason, Now = UtcInstant(now) },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return changed > 0 ? OutputFailureOutcome.Applied : OutputFailureOutcome.AlreadyTerminal;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<JobFinalizationResult> FinalizeJobAsync(
        string reportJobId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var jobStatus = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                SelectJobStatusSql + " FOR UPDATE",
                new { ReportJobId = reportJobId },
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (jobStatus is null
                || JobWireValues.ParseStoredStatus(jobStatus) is ReportJobStatus.Succeeded or ReportJobStatus.Failed or ReportJobStatus.Cancelled)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JobFinalizationResult(JobFinalizationOutcome.AlreadyTerminal, null);
            }

            var outputs = (await connection.QueryAsync<OutputStatusRow>(new CommandDefinition(
                SelectOutputsForFinalizationSql,
                new { ReportJobId = reportJobId },
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            var failed = outputs.FirstOrDefault(output => JobWireValues.ParseStoredOutputStatus(output.Status) == OutputStatus.Failed);
            var allSucceeded = outputs.Count > 0
                && outputs.All(output => JobWireValues.ParseStoredOutputStatus(output.Status) == OutputStatus.Succeeded);
            if (failed is null && !allSucceeded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new JobFinalizationResult(JobFinalizationOutcome.AlreadyTerminal, null);
            }

            var status = failed is null ? ReportJobStatus.Succeeded : ReportJobStatus.Failed;
            await connection.ExecuteAsync(new CommandDefinition(
                FinalizeJobSql,
                new
                {
                    ReportJobId = reportJobId,
                    Status = JobWireValues.ToStoredText(status),
                    Code = failed?.ErrorCode,
                    Reason = failed is null ? null : failed.ErrorReason ?? "output-failed",
                    Now = UtcInstant(now),
                },
                transaction,
                CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new JobFinalizationResult(JobFinalizationOutcome.Applied, status)
            {
                ErrorCode = failed?.ErrorCode,
            };
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<AttemptCauseOutcome> RecordAttemptCauseAsync(
        AttemptCause cause,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cause);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                InsertAttemptCauseSql,
                new
                {
                    OutputId = cause.OutputId,
                    BlockIndex = cause.BlockIndex,
                    Attempt = cause.Attempt,
                    Cause = cause.Cause,
                    RecordedAt = UtcInstant(cause.RecordedAt),
                },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return changed > 0 ? AttemptCauseOutcome.Recorded : AttemptCauseOutcome.AlreadyRecorded;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<AttemptCause?> FindLastAttemptCauseAsync(
        string outputId,
        int blockIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<AttemptCauseRow>(new CommandDefinition(
                SelectLastAttemptCauseSql,
                new { OutputId = outputId, BlockIndex = blockIndex },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row?.ToRecord();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    private static async Task<GuardRow?> GuardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string outputId,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<GuardRow>(new CommandDefinition(
            GuardSql,
            new { OutputId = outputId },
            transaction,
            CommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static bool AllowsCommit(GuardRow guard)
    {
        var job = JobWireValues.ParseStoredStatus(guard.JobStatus);
        var output = JobWireValues.ParseStoredOutputStatus(guard.OutputStatus);
        return job is not (ReportJobStatus.Cancelling or ReportJobStatus.Cancelled) && output == OutputStatus.Running;
    }

    private sealed class GuardRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string JobStatus { get; set; } = string.Empty;

        public string OutputStatus { get; set; } = string.Empty;
    }

    // The cause row Dapper maps: the five columns the read selects, the instant in UTC.
    private sealed class AttemptCauseRow
    {
        public string OutputId { get; set; } = string.Empty;

        public int BlockIndex { get; set; }

        public int Attempt { get; set; }

        public string Cause { get; set; } = string.Empty;

        public DateTime RecordedAt { get; set; }

        public AttemptCause ToRecord() => new(OutputId, BlockIndex, Attempt, Cause, Utc(RecordedAt));
    }

    private sealed class OutputStatusRow
    {
        public string OutputId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }

        public string? ErrorReason { get; set; }
    }

    private sealed class PartitionRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public int PartitionerVersion { get; set; }

        public int BlockCount { get; set; }

        public long RecordCount { get; set; }

        public long RecordBytes { get; set; }

        public string IndexObjectKey { get; set; } = string.Empty;

        public string IndexSha256 { get; set; } = string.Empty;

        public long IndexSizeBytes { get; set; }

        public DateTime CommittedAt { get; set; }

        public string? PdfRefusalCode { get; set; }

        public string? PdfRefusalReason { get; set; }

        public PartitionRecord ToRecord() => new(
            ReportJobId,
            PartitionerVersion,
            BlockCount,
            RecordCount,
            RecordBytes,
            IndexObjectKey,
            IndexSha256,
            IndexSizeBytes,
            Utc(CommittedAt),
            PdfRefusalCode,
            PdfRefusalReason);
    }

    private sealed class PartCommitRow
    {
        public string OutputId { get; set; } = string.Empty;

        public int BlockIndex { get; set; }

        public string ObjectKey { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public int PageCount { get; set; }

        public string TemplatePackageSha256 { get; set; } = string.Empty;

        public DateTime CommittedAt { get; set; }

        public string? Presentation { get; set; }

        public PartCommit ToRecord() => new(
            OutputId,
            BlockIndex,
            ObjectKey,
            Sha256,
            SizeBytes,
            PageCount,
            TemplatePackageSha256,
            Utc(CommittedAt),
            PresentationProvenance.Parse(Presentation));
    }

    // The artifact row: the join of the job and its artifact, whose artifact columns are all
    // null for a job that has published nothing yet. A column of a published artifact is never
    // absent, so a missing one is a corrupt row and a fault rather than a default.
    private sealed class ArtifactRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string? ArtifactId { get; set; }

        public string? Format { get; set; }

        public string? MediaType { get; set; }

        public string? ObjectKey { get; set; }

        public string? Sha256 { get; set; }

        public long? SizeBytes { get; set; }

        public DateTime? CreatedAt { get; set; }

        public DateTime? ExpiresAt { get; set; }

        public ArtifactView ToView() => new(
            Stored(ArtifactId),
            ReportJobId,
            JobWireValues.ParseStoredFormat(Stored(Format)),
            Stored(MediaType),
            Stored(ObjectKey),
            Stored(Sha256),
            SizeBytes ?? throw new InvalidOperationException("the stored artifact carries no size"),
            Instant(CreatedAt),
            Instant(ExpiresAt));

        private static string Stored(string? value) =>
            value ?? throw new InvalidOperationException("the stored artifact is missing a column of its own");

        private static DateTimeOffset Instant(DateTime? value) =>
            value is { } instant ? Utc(instant) : throw new InvalidOperationException("the stored artifact is missing an instant of its own");
    }
}
