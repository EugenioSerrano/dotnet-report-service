using ReportService.Jobs;

namespace ReportService.Testing;

/// <summary>
/// The progress counters and the owner's status read of the in-memory store, under
/// the same lock and with the same semantics as the database: a counter only rises,
/// a value above a known total is ignored, a terminal job is frozen, the totals are
/// set once and the read is scoped by the consumer that owns the job. The database's
/// own checks are mirrored here, so a write that would break one throws exactly as
/// the row would be refused there.
/// </summary>
public sealed partial class InMemoryJobStore
{
    private readonly Dictionary<string, JobProgress> _progress = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<ReportJobStatusView?> FindStatusAsync(
        string reportJobId,
        string consumerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        ArgumentNullException.ThrowIfNull(consumerId);
        Interlocked.Increment(ref _findStatusCalls);

        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job)
                || !string.Equals(job.ConsumerId, consumerId, StringComparison.Ordinal))
            {
                return Task.FromResult<ReportJobStatusView?>(null);
            }

            var progress = ProgressOf(reportJobId);
            var outputs = _outputs.Values
                .Where(output => string.Equals(output.ReportJobId, reportJobId, StringComparison.Ordinal))
                .OrderBy(output => Array.IndexOf(FormatOrder, output.Format))
                .Select(output => new ReportOutputStatusView(
                    output.Format,
                    output.Status,
                    output.ErrorCode,
                    _artifacts.TryGetValue(output.OutputId, out var artifact) ? artifact.ArtifactId : null))
                .ToList();

            return Task.FromResult<ReportJobStatusView?>(new ReportJobStatusView(
                job.ReportJobId,
                job.Status,
                job.IsTerminal ? null : ParseStage(job.Stage),
                progress.RecordsRead,
                progress.RecordsTotal,
                progress.BlocksCompleted,
                progress.BlocksTotal,
                progress.PagesGenerated,
                job.ErrorCode,
                job.CreatedAt,
                job.StartedAt,
                job.FinishedAt,
                outputs));
        }
    }

    /// <inheritdoc />
    public Task<ProgressWriteOutcome> RecordRecordsReadAsync(
        string reportJobId,
        long recordsRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportJobId);
        Interlocked.Increment(ref _progressWriteCalls);
        if (ProgressWriteFailure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            if (!_jobs.TryGetValue(reportJobId, out var job) || job.IsTerminal)
            {
                return Task.FromResult(ProgressWriteOutcome.Unchanged);
            }

            var progress = ProgressOf(reportJobId);
            if (recordsRead <= progress.RecordsRead
                || (progress.RecordsTotal is { } total && recordsRead > total))
            {
                return Task.FromResult(ProgressWriteOutcome.Unchanged);
            }

            _progress[reportJobId] = progress with { RecordsRead = recordsRead };
            return Task.FromResult(ProgressWriteOutcome.Raised);
        }
    }

    /// <summary>The fixed order the outputs of an answer are listed in.</summary>
    private static readonly OutputFormat[] FormatOrder =
        [OutputFormat.Pdf, OutputFormat.Html, OutputFormat.Xlsx, OutputFormat.Markdown];

    private JobProgress ProgressOf(string reportJobId) =>
        _progress.TryGetValue(reportJobId, out var progress) ? progress : JobProgress.Empty;

    private static ReportJobStage? ParseStage(string? stage) =>
        stage is null ? null : JobWireValues.ParseStoredStage(stage);

    // The counters a job carries, in the same shape the columns have. The empty value is
    // the state every accepted job starts in: no records, no totals, no page.
    private sealed record JobProgress(
        long RecordsRead,
        long? RecordsTotal,
        int BlocksCompleted,
        int? BlocksTotal,
        long PagesGenerated)
    {
        public static readonly JobProgress Empty = new(0, null, 0, null, 0);
    }
}
