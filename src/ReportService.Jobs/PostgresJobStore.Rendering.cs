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
             index_object_key, index_sha256, index_size_bytes, committed_at)
        VALUES
            (@ReportJobId, @PartitionerVersion, @BlockCount, @RecordCount, @RecordBytes,
             @IndexObjectKey, @IndexSha256, @IndexSizeBytes, @CommittedAt)
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
               committed_at        AS "CommittedAt"
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
             template_package_sha256, committed_at)
        VALUES
            (@OutputId, @BlockIndex, @ObjectKey, @Sha256, @SizeBytes, @PageCount,
             @TemplatePackageSha256, @CommittedAt)
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
               committed_at            AS "CommittedAt"
        FROM report_service.pdf_part_commits
        WHERE output_id = @OutputId AND block_index = @BlockIndex
        """;

    private const string InsertArtifactSql = """
        INSERT INTO report_service.artifacts
            (artifact_id, report_job_id, output_id, format, media_type, object_key, sha256,
             size_bytes, page_count, created_at)
        VALUES
            (@ArtifactId, @ReportJobId, @OutputId, @Format, @MediaType, @ObjectKey, @Sha256,
             @SizeBytes, @PageCount, @CreatedAt)
        ON CONFLICT (output_id) DO NOTHING
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
           SET status = 'FAILED', error_code = @Code, finished_at = @Now
         WHERE output_id = @OutputId AND status IN ('PENDING', 'RUNNING')
        """;

    private const string SelectOutputsForFinalizationSql = """
        SELECT output_id AS "OutputId", status AS "Status", error_code AS "ErrorCode"
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
    public async Task<OutputFailureOutcome> FailOutputAsync(
        string outputId,
        string code,
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
                new { OutputId = outputId, Code = code, Now = UtcInstant(now) },
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
                    Reason = failed is null ? null : "output-failed",
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

    // The guard of every commit that makes a result visible: a job that is ending or has
    // ended publishes nothing, and neither does an output that is no longer running.
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

    private sealed class OutputStatusRow
    {
        public string OutputId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }
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

        public PartitionRecord ToRecord() => new(
            ReportJobId,
            PartitionerVersion,
            BlockCount,
            RecordCount,
            RecordBytes,
            IndexObjectKey,
            IndexSha256,
            IndexSizeBytes,
            Utc(CommittedAt));
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

        public PartCommit ToRecord() => new(
            OutputId,
            BlockIndex,
            ObjectKey,
            Sha256,
            SizeBytes,
            PageCount,
            TemplatePackageSha256,
            Utc(CommittedAt));
    }
}
