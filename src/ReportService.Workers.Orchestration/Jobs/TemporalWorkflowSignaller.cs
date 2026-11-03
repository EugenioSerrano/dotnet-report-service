using System.Diagnostics.CodeAnalysis;
using ReportService.ServiceDefaults.Temporal;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The production seam: it reads the process's current engine connection on every use and
/// nudges the job's execution by its workflow identifier. The identifier is the handle
/// across a continued run — the wait may continue as a new run of the same execution — so
/// the nudge reaches whichever run is current. An identifier the engine does not know, or
/// an execution that has closed, answers <see langword="false"/> and changes nothing.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Workflow-engine client adapter; verified against a real engine in the integration cycle")]
public sealed class TemporalWorkflowSignaller(TemporalClientHolder holder) : IWorkflowSignaller
{
    /// <summary>How long one engine call may take.</summary>
    public static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The engine's "no such execution" status.</summary>
    private const int NotFoundStatus = 5;

    /// <inheritdoc />
    public async Task<bool> SignalInputChangedAsync(string workflowId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);

        var client = holder.Current?.Client ?? throw new InvalidOperationException("no engine connection is current.");
        try
        {
            await client.GetWorkflowHandle<ReportJobWorkflow>(workflowId)
                .SignalAsync(workflow => workflow.OnInputChangedAsync())
                .WaitAsync(RpcTimeout, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RpcException exception) when ((int)exception.Code == NotFoundStatus)
        {
            return false;
        }
    }
}
