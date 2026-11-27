using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.TemplatePackages.Packages;
using ReportService.Templates;
using ReportService.Workers.Pdf.Capacity;

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
        builder.AddDocumentCapacity();
        services.TryAddSingleton<IJobStore, PostgresJobStore>();
        services.TryAddSingleton<TakenTemplatePackageReader>();
        services.TryAddSingleton<PreparedTemplateCache>();
        services.TryAddSingleton(static _ => TemplateEngineAdapters.Registry());
        services.TryAddSingleton(services => new BlockComposer(
            services.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<BlockVerifier>();
        services.TryAddSingleton<PdfRenderingMetrics>();
        services.TryAddSingleton<BrowserSession>();
        services.TryAddSingleton(services => new ChromiumPdfPrinter(
            services.GetRequiredService<BrowserSession>(),
            services.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IPdfPrinter>(services => services.GetRequiredService<ChromiumPdfPrinter>());
        services.TryAddSingleton(services => new BlockRenderer(
            services.GetRequiredService<BlockComposer>(),
            services.GetRequiredService<ChromiumPdfPrinter>(),
            services.GetRequiredService<BlockVerifier>(),
            services.GetRequiredService<TemplateEngineAdapterRegistry>()));

        // The render step asks for the one renderer by its contract: a process that registered
        // the implementation alone could not construct the step at all, and the process would
        // refuse to start rather than print a document.
        services.TryAddSingleton<IBlockRenderer>(services => services.GetRequiredService<BlockRenderer>());
        services.TryAddSingleton(GlobalisationProvenance.Current);
        // The activity is resolved with the process's own dependencies and then given the
        // watchdog: the watchdog is worker-internal, so it is not a public constructor
        // parameter of the step, and no other process can install one.
        services.AddSingleton(provider =>
        {
            var activity = ActivatorUtilities.CreateInstance<RenderPdfBlockActivity>(provider);
            activity.Watchdog = provider.GetRequiredService<RenderWatchdog>();
            return activity;
        });

        // The third registration on the queue: the same engine worker serves it, under
        // the one concurrency the PDF queue, the take and the resolution already carry.
        var queue = RenderPdfBlockOptions.ServedQueue(ServedClassOptions.Read(builder));

        // The schedule-to-start latency of every attempt the queue's worker takes - the block,
        // the take and the resolution - recorded once per attempt; the three registrations share
        // one worker, so the interceptor is installed by this one alone.
        services.TryAddSingleton(static _ => new ScheduleToStartRecorder(
            MeterIdentity.ForHost(TelemetryDefaults.MeterName),
            "render",
            static taskQueue => JobWireValues.ToWire(PriorityTaskQueues.ClassOf(taskQueue))));
        services.AddSingleton(provider => new TaskQueueWork(
            queue,
            options =>
            {
                options.MaxConcurrentActivities = 1;
                options.AddAllActivities(provider.GetRequiredService<RenderPdfBlockActivity>());
                options.Interceptors = [.. options.Interceptors ?? [], new ScheduleToStartInterceptor(provider.GetRequiredService<ScheduleToStartRecorder>())];
            }));
        return builder;
    }
}
