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
using ReportService.TemplateSource.Take;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// What this worker registers on the queue it declares: the take, and everything
/// the take runs on — the settings of the configured source, its typed client,
/// the manifest reader, the taken-version store, the object store the process
/// already has, the conflict warning and the checks registered so far (none
/// today). One work registration, one options object and one engine worker serve
/// <c>report-pdf-rendering</c>, and that registration bounds the queue to one
/// activity at a time — the PDF worker's own rule. The source's settings are
/// validated at start, so a missing repository stops the process naming the
/// setting instead of failing at the first take.
/// </summary>
public static class TemplateTakeRegistration
{
    /// <summary>The name of the HTTP client the source is read through.</summary>
    public const string TemplateSourceClientName = "template-source";
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
        builder.Services.AddSingleton<TemplateTakeMetrics>();
        builder.Services.AddSingleton(services => new TemplateVersionConflict(
            services.GetRequiredService<ILoggerFactory>().CreateLogger(TemplateVersionConflict.LogCategory)));
        builder.Services.AddHttpClient(TemplateSourceClientName);
        builder.Services.AddSingleton<ITemplateSource>(services => new GitHubTemplateSource(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(TemplateSourceClientName),
            services.GetRequiredService<IOptions<TemplateSourceOptions>>().Value));
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
