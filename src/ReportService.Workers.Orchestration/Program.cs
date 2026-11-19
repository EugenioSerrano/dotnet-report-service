using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Data;
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

        // The limit that governs this process, once, before it serves: the effective
        // record count, the byte bound, and a configuration above the maximum that has
        // no effect.
        app.Logger.LogInformation(
            "{SingleBlockLimitStartupLine}",
            app.Services.GetRequiredService<SingleBlockLimit>().DescribeStartupLine());
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
        app.MapStorageDiagnostics();
        app.MapTelemetryDiagnostics();
        app.Run();
    }
}
