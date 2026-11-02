using System.Diagnostics.CodeAnalysis;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Orchestration.Jobs;
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
    public async Task<WorkflowStart> StartUploadVerificationAsync(string workflowId, string uploadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        return await StartAsync(
            workflowId,
            (UploadVerificationWorkflow workflow) => workflow.RunAsync(new UploadVerificationInput(uploadId)),
            UploadVerificationWorkflow.OrchestrationQueue,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkflowStart> StartReportJobAsync(string workflowId, string reportJobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        return await StartAsync(
            workflowId,
            (ReportJobWorkflow workflow) => workflow.RunAsync(new ReportJobInput(reportJobId)),
            ReportJobWorkflow.OrchestrationQueue,
            cancellationToken).ConfigureAwait(false);
    }

    // One start, whatever the workflow: the identifier is stable, a second start of it is
    // refused by the engine and served by the execution already there.
    private async Task<WorkflowStart> StartAsync<TWorkflow>(
        string workflowId,
        System.Linq.Expressions.Expression<Func<TWorkflow, Task>> workflowRun,
        string taskQueue,
        CancellationToken cancellationToken)
    {
        var client = Current();
        try
        {
            var handle = await client.StartWorkflowAsync(
                    workflowRun,
                    new WorkflowOptions
                    {
                        Id = workflowId,
                        TaskQueue = taskQueue,
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
