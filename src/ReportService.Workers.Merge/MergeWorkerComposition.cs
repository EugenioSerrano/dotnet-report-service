using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Merge.Assembly;

namespace ReportService.Workers.Merge;

/// <summary>
/// The merge worker's whole service graph, in one place: the telemetry and the
/// health endpoint, the metadata database, the object store, the merge task queue
/// and the one assembly step it serves. The process builds this graph at start and
/// so do the tests, so a service that asks for a setting the registrations do not
/// publish is refused before the process serves anything.
/// </summary>
public static class MergeWorkerComposition
{
    /// <summary>Registers everything the merge worker runs.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder Configure(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceDefaults(ServiceNames.MergeWorker, MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        builder.AddServiceConfiguration();
        builder.AddMetadataDatabase();

        // The stores the assembly step resolves are built over the pool this process owns,
        // exactly as the orchestration worker, the API and the PDF worker register it; the
        // metadata database's settings are the ones the service configuration validated.
        builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
            services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        builder.AddObjectStorage("merge-worker");

        // The one queue this process declares; the assembly step is its only work.
        builder.AddWorkflowEngine("merge-worker", [AssemblyStepOptions.Queue]);
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());
        builder.AddMergeWorkerWork();
        return builder;
    }
}
