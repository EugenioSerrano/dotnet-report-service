using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Pdf.PageCodes.Hosting;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.Jobs;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Templates;
using ReportService.Workers.Pdf.Capacity;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// What this worker registers on the queue it declares: the take, and everything
/// the take runs on - the template source's own object store, on its own root and
/// read-only, the class read through that instance over the deployment's provider,
/// the manifest reader, the taken-version store, the conflict warning, the checks
/// this build registers and their sample render's settings. The recorded engine
/// versions are fed from the adapters at composition. The source's settings are
/// validated at start, so a root that is missing or a class that cannot be read
/// stops the process naming the setting instead of failing at the first take. One
/// work registration, one options object and one engine worker serve
/// <c>report-pdf-rendering</c>, and that registration bounds the queue to one
/// activity at a time - the PDF worker's own rule.
/// </summary>
public static class TemplateTakeRegistration
{
    /// <summary>The layout of the class the source reads, as this build writes its keys.</summary>
    public static readonly TemplateSourceClassLayout SourceClassLayout = new(
        TemplateSourceKeys.ClassPrefix,
        TemplateSourceKeys.PublishedSegment,
        TemplateSourceKeys.DescriptorSuffix,
        TemplateSourceKeys.DescriptorHashLength);

    /// <summary>Registers the take's work and the settings it runs under.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddTemplateTakeWork(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The recorded engine versions are the adapters this process registered, read once at
        // composition: before this line the catalog answers the pins, after it the adapters.
        TemplateEngineCatalogFeed.Apply();

        builder.AddValidatedConfiguration<TemplateSourceOptions>(TemplateSourceOptions.SectionPath);
        builder.AddValidatedConfiguration<SampleRenderOptions>(SampleRenderOptions.SectionPath);
        builder.AddTemplateSourceStorage(SourceClassLayout, PublicationChecks.DescriptorVersions);
        builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
            services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<TemplateManifestReader>();
        builder.Services.AddSingleton<TemplateSourceMetrics>();
        builder.Services.AddSingleton<TemplateTakeMetrics>();
        builder.Services.AddSingleton(services => new TemplateVersionConflict(
            services.GetRequiredService<ILoggerFactory>().CreateLogger(TemplateVersionConflict.LogCategory)));
        builder.Services.AddSingleton<ITemplateSourceReader>(services => new StorageTemplateSource(
            services.GetRequiredKeyedService<IObjectStore>(TemplateSourceStorage.StoreServiceKey),
            services.GetRequiredService<IOptions<TemplateSourceOptions>>().Value,
            services.GetRequiredService<TemplateSourceMetrics>(),
            services.GetRequiredService<ILogger<StorageTemplateSource>>()));
        builder.Services.AddSingleton<ITemplateVersionStore, PostgresTemplateVersionStore>();
        builder.Services.AddSingleton<TemplateTake>();
        builder.Services.AddPageCodeGenerators(builder.Configuration);
        builder.AddDocumentCapacity();
        builder.Services.TryAddSingleton(static services =>
            new NumberingCapacity(services.GetRequiredService<DocumentCapacity>().MaximumPages));
        builder.Services.AddTemplateVersionChecks();
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
