namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>
/// What a dispatched request was turned into: the execution that serves it. The
/// workflow identifier is the request's own subject, so it is also the answer to
/// "was this already started?"; the run identifier is the engine's, recorded so an
/// operator can find the execution without searching.
/// </summary>
/// <param name="WorkflowId">The identifier of the workflow the request started.</param>
/// <param name="RunId">The engine's identifier of that execution.</param>
/// <param name="WorkerBuild">The version of the worker that dispatched it.</param>
/// <param name="RetryPolicy">A constant summary of the retry policy the work runs under.</param>
/// <param name="RecordedAt">When the reference was recorded, in UTC.</param>
public sealed record ExecutionReference(
    string WorkflowId,
    string RunId,
    string WorkerBuild,
    string RetryPolicy,
    DateTimeOffset RecordedAt);
