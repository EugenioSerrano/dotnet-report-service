using ReportService.ServiceDefaults.Temporal;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;

namespace ReportService.Workers.Orchestration.Telemetry;

/// <summary>
/// The two read-only engine calls the priority dispatch makes, over the connection this worker
/// holds: describing an execution (its pending activities) and describing a task queue (its
/// statistics). The callers take them as delegates, so their logic is tested without an engine.
/// </summary>
public static class EngineReads
{
    /// <summary>The namespace the worker's executions and queues live in.</summary>
    public const string Namespace = "default";

    /// <summary>Describes an execution by its identifier, through <paramref name="holder"/>'s current connection.</summary>
    /// <param name="holder">The worker's engine connection.</param>
    public static Func<string, CancellationToken, Task<DescribeWorkflowExecutionResponse>> DescribeExecution(TemporalClientHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return (workflowId, cancellationToken) => ClientOf(holder).WorkflowService.DescribeWorkflowExecutionAsync(
            new DescribeWorkflowExecutionRequest
            {
                Namespace = ClientOf(holder).Options.Namespace,
                Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = workflowId },
            },
            new RpcOptions { CancellationToken = cancellationToken });
    }

    /// <summary>Describes a task queue's activity side with its statistics, through <paramref name="holder"/>'s current connection.</summary>
    /// <param name="holder">The worker's engine connection.</param>
    public static Func<string, CancellationToken, Task<DescribeTaskQueueResponse>> DescribeTaskQueue(TemporalClientHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return (queue, cancellationToken) => ClientOf(holder).WorkflowService.DescribeTaskQueueAsync(
            new DescribeTaskQueueRequest
            {
                Namespace = ClientOf(holder).Options.Namespace,
                TaskQueue = new Temporalio.Api.TaskQueue.V1.TaskQueue { Name = queue },
                TaskQueueType = TaskQueueType.Activity,
                ReportStats = true,
            },
            new RpcOptions { CancellationToken = cancellationToken });
    }

    private static ITemporalClient ClientOf(TemporalClientHolder holder) =>
        holder.Current?.Client ?? throw new InvalidOperationException("no engine connection is current.");
}
