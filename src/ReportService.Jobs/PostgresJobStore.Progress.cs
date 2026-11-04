using Dapper;
using Npgsql;

namespace ReportService.Jobs;

/// <summary>
/// The progress counters and the owner's status read of a job, in the application's
/// own database. Every counter write is one constant statement that raises the stored
/// value and never lowers it; the read is one statement that returns the job together
/// with its outputs and the identifier of each published one, so a consumer never sees
/// a job and its output from two different instants.
/// </summary>
public sealed partial class PostgresJobStore
{
    private const string FindStatusSql = """
        SELECT j.report_job_id    AS "ReportJobId",
               j.status           AS "Status",
               j.stage            AS "Stage",
               j.records_read     AS "RecordsRead",
               j.records_total    AS "RecordsTotal",
               j.blocks_completed AS "BlocksCompleted",
               j.blocks_total     AS "BlocksTotal",
               j.pages_generated  AS "PagesGenerated",
               j.error_code       AS "ErrorCode",
               j.created_at       AS "CreatedAt",
               j.started_at       AS "StartedAt",
               j.finished_at      AS "FinishedAt",
               o.format           AS "OutputFormat",
               o.status           AS "OutputStatus",
               o.error_code       AS "OutputErrorCode",
               a.artifact_id      AS "ArtifactId"
        FROM report_service.report_jobs j
        JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
        LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
        WHERE j.report_job_id = @ReportJobId AND j.consumer_id = @ConsumerId
        ORDER BY array_position(ARRAY['PDF', 'HTML', 'XLSX', 'MARKDOWN'], o.format)
        """;

    private const string RecordRecordsReadSql = """
        UPDATE report_service.report_jobs
           SET records_read = GREATEST(records_read, @RecordsRead)
         WHERE report_job_id = @ReportJobId
           AND status NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED')
           AND records_read < @RecordsRead
           AND (records_total IS NULL OR @RecordsRead <= records_total)
        """;

    /// <inheritdoc />
    public async Task<ReportJobStatusView?> FindStatusAsync(
        string reportJobId,
        string consumerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(consumerId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var rows = (await connection.QueryAsync<StatusRow>(new CommandDefinition(
                FindStatusSql,
                new { ReportJobId = reportJobId, ConsumerId = consumerId },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            if (rows.Count == 0)
            {
                return null;
            }

            var first = rows[0];
            var status = JobWireValues.ParseStoredStatus(first.Status);
            return new ReportJobStatusView(
                first.ReportJobId,
                status,
                status is ReportJobStatus.Succeeded or ReportJobStatus.Failed or ReportJobStatus.Cancelled
                    ? null
                    : first.Stage is { } stage ? JobWireValues.ParseStoredStage(stage) : null,
                first.RecordsRead,
                first.RecordsTotal,
                first.BlocksCompleted,
                first.BlocksTotal,
                first.PagesGenerated,
                first.ErrorCode,
                Utc(first.CreatedAt),
                first.StartedAt is { } started ? Utc(started) : null,
                first.FinishedAt is { } finished ? Utc(finished) : null,
                [.. rows.Select(row => new ReportOutputStatusView(
                    JobWireValues.ParseStoredFormat(row.OutputFormat),
                    JobWireValues.ParseStoredOutputStatus(row.OutputStatus),
                    row.OutputErrorCode,
                    row.ArtifactId))]);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public async Task<ProgressWriteOutcome> RecordRecordsReadAsync(
        string reportJobId,
        long recordsRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                RecordRecordsReadSql,
                new { ReportJobId = reportJobId, RecordsRead = recordsRead },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return changed > 0 ? ProgressWriteOutcome.Raised : ProgressWriteOutcome.Unchanged;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    // The view's row: the job's own columns repeat on every output row of the one statement.
    private sealed class StatusRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? Stage { get; set; }

        public long RecordsRead { get; set; }

        public long? RecordsTotal { get; set; }

        public int BlocksCompleted { get; set; }

        public int? BlocksTotal { get; set; }

        public long PagesGenerated { get; set; }

        public string? ErrorCode { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? FinishedAt { get; set; }

        public string OutputFormat { get; set; } = string.Empty;

        public string OutputStatus { get; set; } = string.Empty;

        public string? OutputErrorCode { get; set; }

        public string? ArtifactId { get; set; }
    }
}
