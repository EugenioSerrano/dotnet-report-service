using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

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
builder.Services.TryAddSingleton<IUploadStore, PostgresUploadStore>();
builder.Services.TryAddSingleton<UploadVerifier>();

var app = builder.Build();
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
app.MapStorageDiagnostics();
app.MapTelemetryDiagnostics();
app.Run();
