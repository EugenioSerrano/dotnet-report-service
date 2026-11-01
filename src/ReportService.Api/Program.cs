using ReportService.Api.Consumers;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(ServiceNames.Api);
builder.AddServiceConfiguration();
builder.AddMetadataDatabase();
builder.AddConsumerRegistry();
builder.Services.AddHealthChecks();
builder.AddObjectStorage("api");

var app = builder.Build();
app.UseConsumerRegistry();
app.MapStorageDiagnostics();
app.MapTelemetryDiagnostics();
app.MapTelemetryPropagation();
app.Run();
