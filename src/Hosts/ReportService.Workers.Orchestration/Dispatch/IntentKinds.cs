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
}
