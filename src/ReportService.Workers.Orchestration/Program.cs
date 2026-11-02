using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Builder;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Workers.Orchestration;

/// <summary>
/// The orchestration worker: it hosts the orchestration and data task queues, runs
/// one engine worker per queue that has work, verifies confirmed uploads and
/// dispatches the durable requests the API records. It answers readiness through
/// <c>/health</c>, which reports the engine connection and each queue's own state.
/// </summary>
public sealed class OrchestrationWorkerProgram
{
    /// <summary>Starts the process.</summary>
    /// <param name="args">The process's arguments.</param>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        OrchestrationWorkerComposition.Configure(builder);

        var app = builder.Build();
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
        app.MapStorageDiagnostics();
        app.MapTelemetryDiagnostics();
        app.Run();
    }
}
