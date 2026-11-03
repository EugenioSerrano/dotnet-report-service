using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Pdf;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.Workers.Pdf;

/// <summary>
/// The PDF worker's process: the rendering task queue, the browser probe, the
/// take and the by-name resolution. The queue is declared here and served by one
/// engine worker over two registrations — the take's and the resolution's — which
/// bounds the process to one activity at a time. The process now reaches the
/// metadata database, because a taken version is published by a record.
/// </summary>
public sealed class PdfWorkerProgram
{
    /// <summary>Starts the process and serves until it is stopped.</summary>
    /// <param name="args">The command line.</param>
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults(ServiceNames.RenderWorker);
        builder.AddServiceConfiguration();

        // The PDF rendering task queue; the take and the by-name resolution are its
        // registrations.
        builder.AddWorkflowEngine("pdf-worker", [TakeTemplateVersionActivityOptions.Queue]);
        builder.Services.AddSingleton<BrowserProbe>();
        builder.Services.AddHostedService(static services => services.GetRequiredService<BrowserProbe>());
        builder.Services.AddHealthChecks().AddCheck<BrowserHealthCheck>("chromium");
        builder.AddObjectStorage("pdf-worker");
        builder.AddMetadataDatabase();
        builder.AddTemplateTakeWork();
        builder.AddTemplateResolutionWork();

        var app = builder.Build();
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
        app.MapStorageDiagnostics();
        app.MapTelemetryDiagnostics();
        await app.RunAsync();
    }
}
