namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The one engine operation the notification of waiting jobs needs: tell the execution of
/// a job that its upload changed, by the execution's identifier and with nothing else. It
/// is a seam over the engine client the worker already holds, so the notification step is
/// provable without an engine, and it is not a second client, a package or a dispatcher.
/// <para>
/// The nudge carries no payload: the job's run re-reads its upload, so a late, repeated or
/// stale nudge cannot change an outcome by itself. An execution that does not exist yet, or
/// that has already closed, is answered with <see langword="false"/> — that is an ordinary
/// answer, because the wait has its own timer and the truth is always re-read — while any
/// other engine failure is thrown, so the step is retried under its own policy.
/// </para>
/// </summary>
public interface IWorkflowSignaller
{
    /// <summary>Tells the execution behind <paramref name="workflowId"/> that its upload changed.</summary>
    /// <param name="workflowId">The identifier of the job's execution.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the nudge was delivered; <see langword="false"/> when the
    /// engine holds no open execution under that identifier.
    /// </returns>
    Task<bool> SignalInputChangedAsync(string workflowId, CancellationToken cancellationToken);
}
