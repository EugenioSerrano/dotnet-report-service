using ReportService.Data;
using ReportService.Jobs;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The PDF output family as the job workflow starts it: first in the start order, refused at
/// partition when the data step refused the PDF output by the row bound, and the owner of the
/// single-PDF path of runs recorded before multi-output jobs.
/// </summary>
public sealed class PdfOutputFamily : IOutputFamily
{
    /// <inheritdoc />
    public OutputFormat Format => OutputFormat.Pdf;

    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public bool OwnsSingleOutputPath => true;

    /// <inheritdoc />
    public OutputRefusal? RefusalAtPartition(PartitionResult partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return partition.PdfRefusal is { } refusal ? new OutputRefusal(refusal.Code, refusal.Reason) : null;
    }

    /// <inheritdoc />
    public Task StartOutputAsync(OutputStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        return Workflow.ExecuteChildWorkflowAsync(
            (ReportPdfOutputWorkflow workflow) => workflow.RunAsync(
                OutputInput(start.ReportJobId, start.OutputId, start.Dispatch)),
            new ChildWorkflowOptions
            {
                Id = ReportPdfOutputWorkflow.WorkflowIdFor(start.ReportJobId),
                ParentClosePolicy = ParentClosePolicy.RequestCancel,
                // The dependency is recorded at the call site: a cancelled job waits for
                // its child's cleanup before it finalises (ADR-012 §3.10(3)). It is the
                // SDK's default, stated so it cannot be lost silently.
                CancellationType = ChildWorkflowCancellationType.WaitCancellationCompleted,
            });
    }

    /// <inheritdoc />
    public string CodeOf(Exception? failure) => ReportPdfOutputWorkflow.CodeOf(failure);

    /// <summary>
    /// The recorded single-PDF path (runs predating the multi-output path), kept command for
    /// command so their histories replay unchanged: the job moves to running, its one PDF output
    /// is looked up and, when the data step refused it, ended with the reason the pass kept;
    /// otherwise its execution is started. A failure of any step ends the job with the code the
    /// failure carries.
    /// </summary>
    /// <param name="run">What the run needs.</param>
    /// <returns><see langword="true"/> when a failure ended the run, so nothing follows it; otherwise <see langword="false"/>.</returns>
    public async Task<bool> RunSingleOutputAsync(SingleOutputRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var options = run.Options;
        try
        {
            await Workflow.ExecuteActivityAsync(
                (JobTransitionActivities activities) => activities.TransitionJobAsync(
                    new TransitionJobInput(run.ReportJobId, ReportJobStatus.Running)),
                options).ConfigureAwait(true);

            var output = await Workflow.ExecuteActivityAsync(
                (PdfOutputActivities activities) => activities.FindPdfOutputAsync(new FindPdfOutputInput(run.ReportJobId)),
                options).ConfigureAwait(true);

            if (run.Partition.PdfRefusal is { } refusal)
            {
                // The data step refused this output: it ends here, with the output's code
                // and the reason the pass kept, and no child is started for it.
                await Workflow.ExecuteActivityAsync(
                    (JobTransitionActivities activities) => activities.FailOutputAsync(
                        new FailOutputInput(output.OutputId, refusal.Code, refusal.Reason)),
                    options).ConfigureAwait(true);
            }
            else
            {
                await Workflow.ExecuteChildWorkflowAsync(
                    (ReportPdfOutputWorkflow workflow) => workflow.RunAsync(
                        OutputInput(run.ReportJobId, output.OutputId, run.Dispatch)),
                    new ChildWorkflowOptions
                    {
                        Id = ReportPdfOutputWorkflow.WorkflowIdFor(run.ReportJobId),
                        ParentClosePolicy = ParentClosePolicy.RequestCancel,
                    }).ConfigureAwait(true);
            }
        }
        catch (ActivityFailureException failure)
        {
            await run.EndRunAfterFailure(
                run.ReportJobId,
                ReportPdfOutputWorkflow.CodeOf(failure.InnerException),
                "printing-failed",
                ReportJobWorkflow.MessageOf(failure.InnerException),
                options).ConfigureAwait(true);
            return true;
        }
        catch (ChildWorkflowFailureException failure)
        {
            await run.EndRunAfterFailure(
                run.ReportJobId,
                ReportPdfOutputWorkflow.CodeOf(failure.InnerException),
                "printing-failed",
                ReportJobWorkflow.MessageOf(failure.InnerException),
                options).ConfigureAwait(true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// What one PDF output's execution is started with: the job, the output and - when the run
    /// read the job's dispatch - its class and the aging wait. A run that read nothing starts it
    /// exactly as it was recorded.
    /// </summary>
    private static ReportPdfOutputInput OutputInput(string reportJobId, string outputId, JobDispatch? dispatch) =>
        dispatch is null
            ? new ReportPdfOutputInput(reportJobId, outputId)
            : new ReportPdfOutputInput(reportJobId, outputId)
            {
                PriorityClass = dispatch.PriorityClass,
                AgingWaitSeconds = dispatch.AgingWaitSeconds,
            };
}
