using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Uploads;
using ReportService.Workers.Orchestration;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

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
        builder.AddServiceDefaults(ServiceNames.OrchestrationWorker);
        builder.AddServiceConfiguration();
        builder.AddMetadataDatabase();

        // The orchestration and data task queues; work is registered on them as the product gains it.
        builder.AddWorkflowEngine("orchestration-worker", ["report-orchestration", "report-data"]);
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());
        builder.AddObjectStorage("orchestration-worker");

        // Verifying an upload: the size limits it enforces, the records it reads and
        // writes, and the instruments of the work it does.
        builder.AddValidatedConfiguration<VerificationOptions>(VerificationOptions.SectionPath);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<OrchestrationMetrics>();
        builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
            services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        builder.Services.TryAddSingleton<IUploadStore, PostgresUploadStore>();
        builder.Services.TryAddSingleton<UploadVerifier>();

        // The verification workflow and its attempt, and the dispatching of the durable
        // requests the API records, with the settings both run under.
        builder.AddUploadVerificationWork();
        builder.AddIntentDispatch();

        var app = builder.Build();
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
        app.MapStorageDiagnostics();
        app.MapTelemetryDiagnostics();
        app.Run();
    }
}
