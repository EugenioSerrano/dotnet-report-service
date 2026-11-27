using Dapper;

namespace ReportService.Jobs;

/// <summary>The job's effective class, as the orchestration reads and moves it.</summary>
public sealed partial class PostgresJobStore : IJobPriorityStore
{
    private const string ReadEffectiveClassSql =
        "SELECT effective_priority_class FROM report_service.report_jobs WHERE report_job_id = @ReportJobId";

    // One guarded statement: only a low job moves, so a repeated promotion changes nothing.
    private const string PromoteEffectiveClassSql =
        """
        UPDATE report_service.report_jobs
           SET effective_priority_class = 'normal'
         WHERE report_job_id = @ReportJobId
           AND effective_priority_class = 'low'
        """;

    /// <inheritdoc />
    public async Task<JobPriority?> ReadEffectiveClassAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            ReadEffectiveClassSql,
            new { ReportJobId = reportJobId },
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return stored is null ? null : JobWireValues.ParseStoredPriority(stored);
    }

    /// <inheritdoc />
    public async Task<bool> PromoteEffectiveClassAsync(string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var moved = await connection.ExecuteAsync(new CommandDefinition(
            PromoteEffectiveClassSql,
            new { ReportJobId = reportJobId },
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return moved > 0;
    }
}
