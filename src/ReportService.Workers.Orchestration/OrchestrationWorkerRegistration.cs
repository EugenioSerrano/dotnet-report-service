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
        builder.AddValidatedConfiguration<UploadExpiryOptions>(UploadExpiryOptions.SectionPath);

        // The two ends of a verification have to fit together: the deadline the upload is
        // expired by may not be shorter than one attempt of the verification, or a healthy
        // attempt would be cut off by its own upload's deadline.
        builder.AddSettingsCheck(DeadlineAgainstStartToClose);

        builder.Services.AddSingleton<UploadVerificationActivities>();
        builder.Services.AddHostedService<UploadExpirySweeper>();

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
                // configured bounds of one verification are published to the workflow type
                // here, once, while the process plans its workers. The verification's
                // schedule-to-close is the upload's own deadline, so the engine stops
                // retrying when that deadline passes.
                UploadVerificationWorkflow.Configure(
                    provider.GetRequiredService<IOptions<UploadVerificationOptions>>().Value.StartToClose,
                    provider.GetRequiredService<IOptions<VerificationOptions>>().Value.VerificationDeadline);
                options.AddAllActivities(provider.GetRequiredService<UploadVerificationActivities>());
            }));

        return builder;
    }

    private static IReadOnlyList<ConfigurationProblem> DeadlineAgainstStartToClose(IServiceProvider services)
    {
        var verification = services.GetRequiredService<IOptions<UploadVerificationOptions>>().Value;
        var deadline = services.GetRequiredService<IOptions<VerificationOptions>>().Value.VerificationDeadline;
        return verification.DeadlineProblem(deadline) is { } problem ? [problem] : [];
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
