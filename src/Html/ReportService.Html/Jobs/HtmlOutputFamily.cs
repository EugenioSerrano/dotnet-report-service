using ReportService.Data;
using ReportService.Jobs;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The HTML output family as the job workflow starts it: second in the start order and never
/// refused at partition - its capability refusal lives in its own assembly step, so its execution
/// is always started when the job requests it.
/// </summary>
public sealed class HtmlOutputFamily : IOutputFamily
{
    /// <inheritdoc />
    public OutputFormat Format => OutputFormat.Html;

    /// <inheritdoc />
    public int Order => 2;

    /// <inheritdoc />
    public OutputRefusal? RefusalAtPartition(PartitionResult partition) => null;

    /// <inheritdoc />
    public Task StartOutputAsync(OutputStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        return Workflow.ExecuteChildWorkflowAsync(
            (ReportHtmlOutputWorkflow workflow) => workflow.RunAsync(
                new ReportHtmlOutputInput(start.ReportJobId, start.OutputId)),
            new ChildWorkflowOptions
            {
                Id = ReportHtmlOutputWorkflow.WorkflowIdFor(start.ReportJobId),
                ParentClosePolicy = ParentClosePolicy.RequestCancel,
                CancellationType = ChildWorkflowCancellationType.WaitCancellationCompleted,
            });
    }

    /// <inheritdoc />
    public string CodeOf(Exception? failure) => ReportHtmlOutputWorkflow.CodeOf(failure);
}
