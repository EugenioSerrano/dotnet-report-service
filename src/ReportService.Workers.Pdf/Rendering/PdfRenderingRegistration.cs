using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.ServiceDefaults.Temporal;
using ReportService.TemplatePackages.Packages;
using ReportService.Templates;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// What the PDF worker registers for printing: the one composer, printer and
/// verifier the process runs, the cache of prepared packages, the reader of a taken
/// copy, the render step and its registration on the queue the process declares.
/// The render registration joins the take's and the resolution's on the same queue,
/// so the three share one engine worker and the queue stays at one activity at a
/// time - one document is printed per worker.
/// </summary>
public static class PdfRenderingRegistration
{
    /// <summary>Registers the render's work and everything it runs on.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddPdfRenderingWork(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJobStore, PostgresJobStore>();
        services.TryAddSingleton<TakenTemplatePackageReader>();
        services.TryAddSingleton<PreparedTemplateCache>();
        services.TryAddSingleton<ITemplateEngineAdapter, ScribanTemplateAdapter>();
        services.TryAddSingleton(services => new BlockComposer(
            services.GetRequiredService<ITemplateEngineAdapter>(),
            services.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<BlockVerifier>();
        services.TryAddSingleton<BrowserSession>();
        services.TryAddSingleton<IPdfPrinter>(services => new ChromiumPdfPrinter(
            services.GetRequiredService<BrowserSession>(),
            services.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(services => new BlockRenderer(
            services.GetRequiredService<BlockComposer>(),
            services.GetRequiredService<ChromiumPdfPrinter>(),
            services.GetRequiredService<BlockVerifier>()));
        services.AddSingleton<RenderPdfBlockActivity>();

        // The third registration on the queue: the same engine worker serves it, under
        // the one concurrency the PDF queue, the take and the resolution already carry.
        services.AddSingleton(provider => new TaskQueueWork(
            RenderPdfBlockOptions.Queue,
            options =>
            {
                options.MaxConcurrentActivities = 1;
                options.AddAllActivities(provider.GetRequiredService<RenderPdfBlockActivity>());
            }));
        return builder;
    }
}
