using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults.Temporal;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Resolution;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// What the by-name resolution registers on the queue this worker declares: the
/// resolution itself, holding the settings of the configured source, the taken
/// versions and the take; the adapter over the real take as the one seam the
/// resolution receives; the resolution's instruments; and the activity on
/// <c>report-pdf-rendering</c>, on the worker the take already serves and under
/// the same rule — one activity at a time. The source's settings are validated at
/// start, so a missing repository stops the process naming the setting instead of
/// failing at the first resolution.
/// </summary>
public static class TemplateResolutionRegistration
{
    /// <summary>Registers the by-name resolution's work and the settings it runs under.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddTemplateResolutionWork(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The adapter is the only implementation of the seam in the process, so a
        // resolution can only ever take a version through the real take.
        builder.Services.AddSingleton<TemplateTakeAdapter>();
        builder.Services.AddSingleton<ITemplateTake>(services => services.GetRequiredService<TemplateTakeAdapter>());
        builder.Services.AddSingleton<TemplateByNameMetrics>();
        builder.Services.AddSingleton(services => new TemplateVersionResolver(
            services.GetRequiredService<ITemplateSource>(),
            services.GetRequiredService<ITemplateVersionStore>(),
            services.GetRequiredService<ITemplateTake>(),
            services.GetRequiredService<TemplateByNameMetrics>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<TemplateVersionResolver>(),
            services.GetRequiredService<IOptions<TemplateSourceOptions>>().Value.ResolveDeadline));
        builder.Services.AddSingleton<ResolveTemplateVersionActivity>();

        // The second registration on the queue: the same engine worker serves it,
        // with the one concurrency the PDF queue runs at.
        builder.Services.AddSingleton(services => new TaskQueueWork(
            TemplateResolutionActivityOptions.Queue,
            options =>
            {
                options.MaxConcurrentActivities = 1;
                options.AddAllActivities(services.GetRequiredService<ResolveTemplateVersionActivity>());
            }));
        return builder;
    }
}
