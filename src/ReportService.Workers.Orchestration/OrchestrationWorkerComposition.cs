using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Telemetry;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration;

/// <summary>
/// The worker's whole service graph, in one place: the telemetry and the health
/// endpoint, the metadata database, the two task queues and their engine workers,
/// the object store, the upload verification with its limits and its instruments,
/// and the dispatching of the durable requests. The process builds this graph at
/// start and so does the suite, so a service that asks for a setting the
/// registrations do not publish is refused before the process serves anything.
/// The order of the calls is the order of the process's start.
/// </summary>
public static class OrchestrationWorkerComposition
{
    /// <summary>Registers everything the worker runs.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder Configure(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceDefaults(ServiceNames.OrchestrationWorker);
        builder.AddServiceConfiguration();
        builder.AddMetadataDatabase();

        // The orchestration and data task queues; work is registered on them as the product gains it.
        builder.AddWorkflowEngine("orchestration-worker", ["report-orchestration", "report-data"]);
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());
        builder.AddObjectStorage("orchestration-worker");

        // Verifying an upload: the size limits it enforces, the records it reads and
        // writes, and the instruments of the work it does.
        builder.AddValidatedConfiguration<VerificationOptions>(VerificationOptions.SectionPath);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<OrchestrationMetrics>();
        builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
            services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        builder.Services.TryAddSingleton<IUploadStore, PostgresUploadStore>();
        builder.Services.TryAddSingleton<UploadVerifier>();

        // The verification workflow and its attempt, and the dispatching of the durable
        // requests the API records, with the settings both run under.
        builder.AddUploadVerificationWork();
        builder.AddIntentDispatch();

        return builder;
    }
}
