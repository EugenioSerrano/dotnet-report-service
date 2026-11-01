using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Pdf;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceConfiguration();

// The PDF rendering task queue; render work is registered on it as the product gains it.
builder.AddWorkflowEngine("pdf-worker", ["report-pdf-rendering"]);
builder.Services.AddSingleton<BrowserProbe>();
builder.Services.AddHostedService(static services => services.GetRequiredService<BrowserProbe>());
builder.Services.AddHealthChecks().AddCheck<BrowserHealthCheck>("chromium");

var app = builder.Build();
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
await app.RunAsync();
