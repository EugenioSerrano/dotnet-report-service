using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Orchestration.Dispatch;
using ReportService.Workers.Orchestration.Uploads;

namespace ReportService.Workers.Orchestration;

/// <summary>
/// What this worker registers on the queues it already declares. Both registrations
/// go through the delivered seam, so a queue gets exactly one engine worker however
/// many things are registered on it: the verification workflow on the orchestration
/// queue, and the verification attempts — with the process's concurrency bound — on
/// the data queue. Nothing here adds a queue or changes the host.
/// </summary>
public static class OrchestrationWorkerRegistration
{
    /// <summary>Registers the verification work and the settings it runs under.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddUploadVerificationWork(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddValidatedConfiguration<UploadVerificationOptions>(UploadVerificationOptions.SectionPath);
        builder.AddValidatedConfiguration<WorkerOptions>(WorkerOptions.SectionPath);
        builder.Services.AddSingleton<UploadVerificationActivities>();

        builder.Services.AddSingleton(_ => new TaskQueueWork(
            UploadVerificationWorkflow.OrchestrationQueue,
            options =>
            {
                options.AddWorkflow<UploadVerificationWorkflow>();
            }));

        builder.Services.AddSingleton(provider => new TaskQueueWork(
            UploadVerificationActivityOptions.DataQueue,
            options =>
            {
                options.MaxConcurrentActivities = provider.GetRequiredService<IOptions<WorkerOptions>>().Value.DataActivitySlots;

                // The delivered host creates workflows without a service provider, so the
                // configured bound of one attempt is published to the workflow type here,
                // once, while the process plans its workers.
                UploadVerificationWorkflow.Configure(provider.GetRequiredService<IOptions<UploadVerificationOptions>>().Value.StartToClose);
                options.AddAllActivities(provider.GetRequiredService<UploadVerificationActivities>());
            }));

        return builder;
    }

    /// <summary>Registers the dispatching of the durable requests and the settings it runs under.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddIntentDispatch(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddValidatedConfiguration<DispatcherOptions>(DispatcherOptions.SectionPath);
        builder.Services.AddSingleton<IIntentStore, PostgresIntentStore>();
        builder.Services.AddSingleton<IWorkflowStarter, TemporalWorkflowStarter>();
        builder.Services.AddHostedService<IntentDispatcher>();
        return builder;
    }
}
