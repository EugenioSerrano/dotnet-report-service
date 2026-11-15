using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// What this worker registers on the queue it declares: the take, and everything
/// the take runs on - the template source's settings, the class read over the
/// process's object store, the manifest reader, the taken-version store, the
/// object store the process already has, the conflict warning and the checks
/// registered so far (none today). One work registration, one options object and
/// one engine worker serve <c>report-pdf-rendering</c>, and that registration
/// bounds the queue to one activity at a time - the PDF worker's own rule.
/// </summary>
public static class TemplateTakeRegistration
{
    /// <summary>Registers the take's work and the settings it runs under.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddTemplateTakeWork(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddValidatedConfiguration<TemplateSourceOptions>(TemplateSourceOptions.SectionPath);
        builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
            services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<TemplateManifestReader>();
        builder.Services.AddSingleton<TemplateSourceMetrics>();
        builder.Services.AddSingleton<TemplateTakeMetrics>();
        builder.Services.AddSingleton(services => new TemplateVersionConflict(
            services.GetRequiredService<ILoggerFactory>().CreateLogger(TemplateVersionConflict.LogCategory)));
        builder.Services.AddSingleton<ITemplateSourceReader>(services => new StorageTemplateSource(
            services.GetRequiredService<IObjectStore>(),
            services.GetRequiredService<IOptions<TemplateSourceOptions>>().Value,
            services.GetRequiredService<TemplateSourceMetrics>(),
            services.GetRequiredService<ILogger<StorageTemplateSource>>()));
        builder.Services.AddSingleton<ITemplateVersionStore, PostgresTemplateVersionStore>();
        builder.Services.AddSingleton<TemplateTake>();
        builder.Services.AddSingleton<TakeTemplateVersionActivity>();

        // One registration on the queue, resolved from the container so the activity
        // holds its settings, with the one concurrency the PDF queue runs at.
        builder.Services.AddSingleton(services => new TaskQueueWork(
            TakeTemplateVersionActivityOptions.Queue,
            options =>
            {
                options.MaxConcurrentActivities = 1;
                options.AddAllActivities(services.GetRequiredService<TakeTemplateVersionActivity>());
            }));
        return builder;
    }
}
