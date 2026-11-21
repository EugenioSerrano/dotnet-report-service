namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>An execution that serves a request: the workflow and the run it names.</summary>
/// <param name="WorkflowId">The identifier of the workflow.</param>
/// <param name="RunId">The engine's identifier of the execution.</param>
public sealed record WorkflowStart(string WorkflowId, string RunId);

/// <summary>
/// The engine operations dispatching needs: ask whether a workflow identifier is
/// already served by an execution, and start one. This is the seam the tests
/// replace, so the dispatcher's own rules are provable without an engine; the
/// production implementation reads the process's current connection on every call,
/// so a connection the host replaced after an engine loss is used from the next call.
/// Each kind of request has its own start, because the workflow it names and the
/// input it takes differ; the identifier is the same in both, chosen by the caller.
/// </summary>
public interface IWorkflowStarter
{
    /// <summary>
    /// Describes the execution behind <paramref name="workflowId"/>.
    /// </summary>
    /// <param name="workflowId">The identifier to look for.</param>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    /// <returns>The execution, running or closed, or <see langword="null"/> when the identifier is unknown.</returns>
    Task<WorkflowStart?> DescribeAsync(string workflowId, CancellationToken cancellationToken);

    /// <summary>Starts the verification of <paramref name="uploadId"/> under <paramref name="workflowId"/>.</summary>
    /// <param name="workflowId">The identifier the execution must have.</param>
    /// <param name="uploadId">The upload to verify.</param>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    Task<WorkflowStart> StartUploadVerificationAsync(string workflowId, string uploadId, CancellationToken cancellationToken);

    /// <summary>Starts the work of <paramref name="reportJobId"/> under <paramref name="workflowId"/>.</summary>
    /// <param name="workflowId">The identifier the execution must have.</param>
    /// <param name="reportJobId">The job to run.</param>
    /// <param name="cancellationToken">The poll's cancellation token.</param>
    Task<WorkflowStart> StartReportJobAsync(string workflowId, string reportJobId, CancellationToken cancellationToken);
}
