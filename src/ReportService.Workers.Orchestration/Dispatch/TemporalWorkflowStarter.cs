using System.Diagnostics.CodeAnalysis;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Orchestration.Uploads;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>
/// The production starter: it reads the process's current engine connection on
/// every use and never keeps a client of its own, so a connection the worker host
/// replaced after an engine loss is used by the next describe and the next start.
/// A start that collides with an existing execution is reported as the execution
/// already there, never as a second one. Every call is bounded in time, so one
/// unreachable engine cannot hold a claimed request beyond the poll's budget.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Workflow-engine client adapter; verified against a real engine in the integration cycle")]
public sealed class TemporalWorkflowStarter(TemporalClientHolder holder) : IWorkflowStarter
{
    /// <summary>How long one engine call may take.</summary>
    public static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The engine's "no such execution" status.</summary>
    private const int NotFoundStatus = 5;

    /// <inheritdoc />
    public async Task<WorkflowStart?> DescribeAsync(string workflowId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        var client = Current();
        try
        {
            var description = await client.GetWorkflowHandle(workflowId)
                .DescribeAsync()
                .WaitAsync(RpcTimeout, cancellationToken)
                .ConfigureAwait(false);
            return new WorkflowStart(workflowId, description.RunId);
        }
        catch (RpcException exception) when ((int)exception.Code == NotFoundStatus)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<WorkflowStart> StartAsync(string workflowId, string uploadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        var client = Current();
        try
        {
            var handle = await client.StartWorkflowAsync(
                    (UploadVerificationWorkflow workflow) => workflow.RunAsync(new UploadVerificationInput(uploadId)),
                    new WorkflowOptions
                    {
                        Id = workflowId,
                        TaskQueue = UploadVerificationWorkflow.OrchestrationQueue,
                        IdConflictPolicy = WorkflowIdConflictPolicy.Fail,
                        IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                    })
                .WaitAsync(RpcTimeout, cancellationToken)
                .ConfigureAwait(false);

            var description = await handle.DescribeAsync().WaitAsync(RpcTimeout, cancellationToken).ConfigureAwait(false);
            return new WorkflowStart(workflowId, description.RunId);
        }
        catch (WorkflowAlreadyStartedException)
        {
            // Another process started this very execution first: the request is served.
            return await DescribeAsync(workflowId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("the workflow was reported as started and cannot be described.");
        }
    }

    // The connection the host last published; none means the engine is not reachable yet.
    private ITemporalClient Current() =>
        holder.Current?.Client ?? throw new InvalidOperationException("no engine connection is current.");
}
