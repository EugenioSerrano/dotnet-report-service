using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.TemplateChecks.Sample;
using ReportService.Workers.Pdf;
using ReportService.Workers.Pdf.Rendering;
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
    /// <summary>The exit code of a process whose start was refused by its configuration.</summary>
    public const int RefusedStartExitCode = 1;

    /// <summary>Starts the process and serves until it is stopped.</summary>
    /// <param name="args">The command line.</param>
    public static async Task Main(string[] args)
    {
        // The entry the in-process test host runs: a start the configuration refuses surfaces
        // from here, which is what the host's callers read.
        var app = BuildHost(args);
        await app.RunAsync();
    }

    /// <summary>
    /// Builds the process's host: the service defaults and their validation, the queue and its
    /// registrations, the browser probe, the render watchdog, the storage, the metadata database,
    /// the take's sample render and the three diagnostic surfaces. One host, built once, so the
    /// container's entry and the in-process host run the same process.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <returns>The built host, not started.</returns>
    public static WebApplication BuildHost(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults(ServiceNames.RenderWorker, MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        builder.AddServiceConfiguration();

        // The PDF rendering task queue of the one priority class this process serves; the
        // take and the by-name resolution are its registrations. A served class the setting
        // does not name stops the process at start.
        builder.AddServedClassCheck(static value => PriorityTaskQueues.TryParseServedClass(value, out _));
        builder.AddWorkflowEngine("pdf-worker", [TakeTemplateVersionActivityOptions.ServedQueue(ServedClassOptions.Read(builder))]);
        builder.Services.AddSingleton<BrowserProbe>();
        builder.Services.AddHostedService(static services => services.GetRequiredService<BrowserProbe>());

        // The render watchdog: its values are the ADR's and are validated at start; the
        // process exit is the seam a test replaces; the health check is served by the one
        // /health surface below.
        builder.Services.AddSingleton(RenderWatchdogOptions.Create());
        builder.AddSettingsCheck(provider => provider.GetRequiredService<RenderWatchdogOptions>().Validate());
        builder.Services.AddSingleton<IProcessExit, EnvironmentProcessExit>();
        builder.Services.AddSingleton<RenderWatchdog>();
        builder.Services.AddHostedService(static services => services.GetRequiredService<RenderWatchdog>());
        builder.Services.AddHealthChecks()
            .AddCheck<BrowserHealthCheck>("chromium")
            .AddCheck<RenderWatchdogHealthCheck>("render-watchdog");
        builder.AddObjectStorage("pdf-worker");
        builder.AddMetadataDatabase();
        builder.AddTemplateTakeWork();
        builder.AddTemplateResolutionWork();
        builder.AddPdfRenderingWork();

        // The take's sample render: the one renderer over the capability registered above,
        // and its instruments. The take resolves the check; the check resolves this port.
        builder.Services.AddSingleton<SampleRenderMetrics>();
        builder.Services.AddSingleton<ISampleRenderer, PdfSampleRenderer>();

        var app = builder.Build();
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.Json });
        app.MapStorageDiagnostics();
        app.MapTelemetryDiagnostics();
        return app;
    }
}
