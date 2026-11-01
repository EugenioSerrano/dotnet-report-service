using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceConfiguration();
builder.AddMetadataDatabase();

// The orchestration and data task queues; work is registered on them as the product gains it.
builder.AddWorkflowEngine("orchestration-worker", ["report-orchestration", "report-data"]);
builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());
builder.AddObjectStorage("orchestration-worker");

var app = builder.Build();
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
app.MapStorageDiagnostics();
app.Run();
