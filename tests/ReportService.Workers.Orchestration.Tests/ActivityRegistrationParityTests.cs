using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage.Filesystem;
using ReportService.Workers.Orchestration.Dispatch;
using ReportService.Workers.Orchestration.Uploads;
using ReportService.Workers.Orchestration.Tests.Jobs;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// What the orchestration worker registers, queue by queue: every workflow and every activity by
/// the name the engine schedules it under. A running execution finds its steps by these names, so
/// the set is fixed here as the worker registered it before its work was split between the job
/// core and the output families - a step that moves keeps its name and its queue, or this fails.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class ActivityRegistrationParityTests
{
    /// <summary>The registrations, queue by queue, as the worker planned them before the split.</summary>
    private static readonly string[] Registered =
    [
        "report-data activity ValidateAndPartition",
        "report-data activity VerifyUpload",
        "report-html activity AssembleHtml",
        "report-orchestration workflow ReportHtmlOutput",
        "report-orchestration workflow ReportJob",
        "report-orchestration workflow ReportPdfOutput",
        "report-orchestration workflow UploadVerification",
        "report-orchestration activity BeginInputWait",
        "report-orchestration activity EndInputWait",
        "report-orchestration activity FailJob",
        "report-orchestration activity FailOutput",
        "report-orchestration activity FinalizeJob",
        "report-orchestration activity FindLastAttemptCause",
        "report-orchestration activity FindOutput",
        "report-orchestration activity FindPdfOutput",
        "report-orchestration activity PinBaseline",
        "report-orchestration activity PinTemplate",
        "report-orchestration activity PrepareTemplate",
        "report-orchestration activity PromoteToNormal",
        "report-orchestration activity PublishHtmlOutput",
        "report-orchestration activity PublishOutput",
        "report-orchestration activity ReadJobDispatch",
        "report-orchestration activity ReadJobState",
        "report-orchestration activity ReadPageCodeDeclaration",
        "report-orchestration activity ReadPdfOutputPartitionFacts",
        "report-orchestration activity ReadPendingDispatch",
        "report-orchestration activity ResolveInput",
        "report-orchestration activity SignalWaitingJobs",
        "report-orchestration activity TransitionJob",
        "report-orchestration activity TransitionOutput",
    ];

    [Fact]
    public void TheWorkerRegistersEveryWorkflowAndActivityUnderItsNameOnItsQueue()
    {
        var root = Directory.CreateTempSubdirectory("worker-registration-");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(Settings(root.FullName));
            OrchestrationWorkerComposition.Configure(builder);

            using var host = builder.Build();
            var registrations = host.Services.GetServices<TaskQueueWork>().ToList();
            var queues = registrations.Select(registration => registration.Queue).Distinct().Order(StringComparer.Ordinal).ToList();
            var registered = new List<string>();
            foreach (var worker in WorkerPlan.Plan(queues, registrations).OrderBy(worker => worker.Queue, StringComparer.Ordinal))
            {
                registered.AddRange(worker.Options.Workflows.Select(workflow => workflow.Name).Order(StringComparer.Ordinal).Select(name => $"{worker.Queue} workflow {name}"));
                registered.AddRange(worker.Options.Activities.Select(activity => activity.Name).Order(StringComparer.Ordinal).Select(name => $"{worker.Queue} activity {name}"));
            }

            Assert.Equal(Registered, registered);
        }
        finally
        {
            root.Delete(true);
        }
    }

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
