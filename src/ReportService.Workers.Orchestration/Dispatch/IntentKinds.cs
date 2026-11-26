namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>
/// The kinds of durable request the process dispatches. Each kind names one
/// workflow, and the value is stored in the database, so it never changes once
/// shipped.
/// </summary>
public static class IntentKinds
{
    /// <summary>The request that verifies one confirmed upload.</summary>
    public const string StartUploadVerification = "start-upload-verification";

    /// <summary>The request that runs one accepted report job.</summary>
    public const string StartJob = "start-job";

    /// <summary>
    /// The request that carries a consumer's cancellation of a running job to its
    /// workflow. The cancel request surface records it together with the move to
    /// <c>CANCELLING</c>; the dispatcher's plan for it is US-010.BOLT-002, so until
    /// then it stays <c>PENDING</c> and is never delivered.
    /// </summary>
    public const string CancelJob = "cancel-job";
}
