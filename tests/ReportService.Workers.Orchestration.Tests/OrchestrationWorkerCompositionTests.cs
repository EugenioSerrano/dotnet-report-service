using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage.Filesystem;
using ReportService.Workers.Orchestration.Dispatch;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Uploads;
using ReportService.Workers.Orchestration.Tests.Jobs;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The worker's own composition, built the way the process builds it: every
/// service registered with the settings record it asks for has to be resolvable,
/// so a settings record that is bound and validated but never published cannot
/// reach a container that only the host builds. Nothing here starts the process;
/// building the container is what the host does before it serves anything.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class OrchestrationWorkerCompositionTests
{
    [Fact]
    public void TheContainerTheWorkerBuildsResolvesEveryServiceItRegisters()
    {
        var root = Directory.CreateTempSubdirectory("worker-composition-");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(Settings(root.FullName));

            // The host validates its container on build in development, which is where a
            // settings record the registrations do not publish is refused; the suite asks
            // for the same validation in every environment.
            builder.ConfigureContainer(new DefaultServiceProviderFactory(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
            OrchestrationWorkerComposition.Configure(builder);

            using var host = builder.Build();

            // Resolved through the container: the verifier takes the size limits, the
            // stores, the clock and its instruments, and nothing here reaches a network.
            Assert.NotNull(host.Services.GetRequiredService<UploadVerifier>());
            Assert.NotNull(host.Services.GetRequiredService<UploadVerificationActivities>());

            // Registered, not constructed: starting the dispatcher would reach the engine.
            Assert.Contains(
                builder.Services,
                descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(IntentDispatcher));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void TheWaitingStepsArePlannedOnTheQueueTheJobWorkflowRunsOn()
    {
        var root = Directory.CreateTempSubdirectory("worker-composition-");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(Settings(root.FullName));
            builder.ConfigureContainer(new DefaultServiceProviderFactory(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
            OrchestrationWorkerComposition.Configure(builder);

            using var host = builder.Build();
            var registrations = host.Services.GetServices<TaskQueueWork>().ToList();
            var plan = WorkerPlan.Plan(
                [ReportJobWorkflow.OrchestrationQueue, "report-data"],
                registrations);

            // One worker per queue, whatever is registered on it: the job's steps and the
            // waiting steps share the orchestration worker and nothing is added to the data one.
            var orchestration = Assert.Single(plan, worker => worker.Queue == ReportJobWorkflow.OrchestrationQueue);
            var activities = orchestration.Options.Activities.Select(activity => activity.Name).ToList();
            Assert.Contains(InputWaitActivities.BeginName, activities);
            Assert.Contains(InputWaitActivities.EndName, activities);
            Assert.Contains(ReportJobActivities.ResolveInputName, activities);
            Assert.Contains(
                orchestration.Options.Workflows,
                workflow => workflow.Name == ReportJobWorkflow.WorkflowType);

            // The settings the wait runs under are published and valid as the process starts.
            var options = host.Services.GetRequiredService<IOptions<InputWaitOptions>>().Value;
            Assert.Empty(options.Validate());
            Assert.Contains(plan, worker => worker.Queue == "report-data");
        }
        finally
        {
            root.Delete(true);
        }
    }

    /// <summary>The settings the process needs, at the values the worker's own defaults carry.</summary>
    private static Dictionary<string, string?> Settings(string storageRoot) => new(StringComparer.Ordinal)
    {
        [DatabaseOptions.ConnectionStringSetting] = "Host=127.0.0.1;Port=5432;Database=report_service;Username=report_service;Password=unused",
        [DatabaseOptions.ExpectedSchemaVersionSetting] = "0003_upload-content-snapshots-and-intents.sql",
        [$"{ObjectStorageSettings.SectionPath}:{nameof(ObjectStorageSettings.Provider)}"] = "filesystem",
        [$"{FilesystemStorageOptions.SectionPath}:{nameof(FilesystemStorageOptions.Root)}"] = storageRoot,
        [$"{FilesystemStorageOptions.SectionPath}:{nameof(FilesystemStorageOptions.CreateIfMissing)}"] = "true",
        [$"{WorkflowEngineOptions.SectionPath}:{nameof(WorkflowEngineOptions.Address)}"] = "127.0.0.1:7233",
        [$"{VerificationOptions.SectionPath}:{VerificationOptions.MaxStoredBytesSetting}"] = "4294967296",
        [$"{VerificationOptions.SectionPath}:{VerificationOptions.MaxUncompressedBytesSetting}"] = "4294967296",
        [$"{UploadVerificationOptions.SectionPath}:{UploadVerificationOptions.StartToCloseSetting}"] = "01:00:00",
        [$"{DispatcherOptions.SectionPath}:{DispatcherOptions.PollIntervalSetting}"] = "00:00:01",
        [$"{DispatcherOptions.SectionPath}:{DispatcherOptions.BatchSizeSetting}"] = "50",
        [$"{DispatcherOptions.SectionPath}:{DispatcherOptions.AlertAfterSetting}"] = "00:05:00",
        [$"{DispatcherOptions.SectionPath}:{DispatcherOptions.ClaimBudgetSetting}"] = "00:00:30",
        [$"{WorkerOptions.SectionPath}:{WorkerOptions.DataActivitySlotsSetting}"] = "2",
    };
}
