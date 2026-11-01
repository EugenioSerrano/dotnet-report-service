namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The name each process reports as its <c>service.name</c>. One constant per
/// process, passed to the telemetry registration by the process itself, so a
/// signal can always be traced back to the process that emitted it.
/// </summary>
public static class ServiceNames
{
    /// <summary>The API.</summary>
    public const string Api = "report-service-api";

    /// <summary>The orchestration worker.</summary>
    public const string OrchestrationWorker = "report-service-orchestration-worker";

    /// <summary>The PDF worker.</summary>
    public const string RenderWorker = "report-service-render-worker";
}
