namespace ReportService.Workers.Orchestration.Dispatch;

/// <summary>
/// The kinds of durable request the process dispatches. One kind exists today —
/// the request a confirmation records — and each kind names one workflow. The
/// value is stored in the database, so it never changes once shipped.
/// </summary>
public static class IntentKinds
{
    /// <summary>The request that verifies one confirmed upload.</summary>
    public const string StartUploadVerification = "start-upload-verification";
}
