using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>
/// The readiness of a worker process against the workflow engine: healthy only
/// when the engine answers on the process's own connection within the timeout
/// and no created engine worker has stopped. Its data names the address, the
/// namespace and the state of each declared task queue — never a credential.
/// </summary>
public sealed class TemporalConnectionHealthCheck(
    TemporalClientHolder holder,
    EngineWorkerStates states,
    IOptions<WorkflowEngineOptions> options,
    TimeSpan timeout) : IHealthCheck
{
    /// <summary>The name the check is registered under.</summary>
    public const string Name = "workflow-engine";

    /// <summary>How long a readiness probe may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var queues = states.Snapshot();
        var data = new Dictionary<string, object>
        {
            ["address"] = options.Value.Address ?? string.Empty,
            ["namespace"] = options.Value.Namespace,
            ["queues"] = queues,
        };

        if (holder.Current is not { } connection)
        {
            return HealthCheckResult.Unhealthy("connecting", data: data);
        }

        if (!await AnswersAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return HealthCheckResult.Unhealthy("engine unreachable", data: data);
        }

        var stopped = queues.Where(q => q.Value == EngineWorkerStates.Stopped).Select(q => q.Key).ToList();
        return stopped.Count > 0
            ? HealthCheckResult.Unhealthy($"worker stopped: {string.Join(", ", stopped)}", data: data)
            : HealthCheckResult.Healthy(data: data);
    }

    private async Task<bool> AnswersAsync(IEngineConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.CheckHealthAsync(timeout, cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
