using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReportService.DataContract;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.TemplatePackages.Records;
using ReportService.Workers.Orchestration.Dispatch;
using ReportService.Workers.Orchestration.Jobs;
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

    /// <summary>
    /// Registers the work one accepted job runs — the job workflow and the two metadata
    /// steps on the orchestration queue — and the records those steps read and write.
    /// </summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddReportJobWork(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IJobStore, PostgresJobStore>();
        builder.AddValidatedConfiguration<InputWaitOptions>(InputWaitOptions.SectionPath);
        builder.Services.AddSingleton<ReportJobActivities>();
        builder.Services.AddSingleton<JobTransitionActivities>();
        builder.Services.AddSingleton<PdfOutputActivities>();
        builder.Services.AddSingleton<InputWaitActivities>();
        builder.Services.AddSingleton<InputWaitSignalActivities>();
        builder.Services.AddSingleton<TemplateResolutionActivities>();

        // The data step reads the contract of the version a job is pinned to: the taken
        // versions and the contracts this worker prepared. The step opens a taken copy
        // through the reader it builds from the object store it already holds.
        builder.Services.TryAddSingleton<ITemplateVersionStore, PostgresTemplateVersionStore>();
        builder.Services.TryAddSingleton<PreparedDataContractCache>();
        builder.Services.AddSingleton<ValidateAndPartitionActivity>();

        // The nudge a finished verification sends to the jobs of its upload goes through the
        // engine connection the worker already holds; the seam is what the tests double.
        builder.Services.TryAddSingleton<IWorkflowSignaller, TemporalWorkflowSignaller>();
        builder.Services.AddSingleton(provider => new TaskQueueWork(
            ReportJobWorkflow.OrchestrationQueue,
            options =>
            {
                // The wait's bounds are published to the workflow type here, once, while the
                // process plans its workers — the delivered pattern the verification uses.
                var inputWait = provider.GetRequiredService<IOptions<InputWaitOptions>>().Value;
                ReportJobWorkflow.Configure(inputWait.Backstop, inputWait.ContinueAsNewAfter);

                options.AddWorkflow<ReportJobWorkflow>();
                options.AddWorkflow<ReportPdfOutputWorkflow>();
                options.AddAllActivities(provider.GetRequiredService<ReportJobActivities>());
                options.AddAllActivities(provider.GetRequiredService<JobTransitionActivities>());
                options.AddAllActivities(provider.GetRequiredService<PdfOutputActivities>());
                options.AddAllActivities(provider.GetRequiredService<InputWaitActivities>());
                options.AddAllActivities(provider.GetRequiredService<InputWaitSignalActivities>());
                options.AddAllActivities(provider.GetRequiredService<TemplateResolutionActivities>());
            }));
        builder.Services.AddSingleton(provider => new TaskQueueWork(
            PdfOutputActivityOptions.DataQueue,
            options =>
            {
                options.MaxConcurrentActivities = provider.GetRequiredService<IOptions<WorkerOptions>>().Value.DataActivitySlots;
                options.AddAllActivities(provider.GetRequiredService<ValidateAndPartitionActivity>());
            }));
        return builder;
    }
}
