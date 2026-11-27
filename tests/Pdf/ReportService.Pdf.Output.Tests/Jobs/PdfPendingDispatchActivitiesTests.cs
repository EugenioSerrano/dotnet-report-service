using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.Workflow.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The aging sample of a PDF output over the in-memory store and a built engine description:
/// render or assembly work scheduled and not started is pending, and the sample reads the job's
/// current class. The cases moved here with the step they test.
/// </summary>
public sealed class PdfPendingDispatchActivitiesTests
{
    private const string JobId = "job-priority";
    private const string OutputId = "output-priority";

    [Fact]
    public void WaitingRenderOrAssemblyWorkIsPendingAndStartedOrOtherWorkIsNot()
    {
        Assert.True(PendingDispatchMapper.AnyWaiting(Describe((PdfOutputStepContracts.RenderPdfBlock, PendingActivityState.Scheduled))));
        Assert.True(PendingDispatchMapper.AnyWaiting(Describe((AssemblePdfStepContracts.AssemblePdf, PendingActivityState.Scheduled))));
        Assert.False(PendingDispatchMapper.AnyWaiting(Describe((PdfOutputStepContracts.RenderPdfBlock, PendingActivityState.Started))));
        Assert.False(PendingDispatchMapper.AnyWaiting(Describe(("PublishOutput", PendingActivityState.Scheduled))));
        Assert.False(PendingDispatchMapper.AnyWaiting(Describe()));
        Assert.True(PendingDispatchMapper.AnyWaiting(Describe(
            (PdfOutputStepContracts.RenderPdfBlock, PendingActivityState.Started),
            (PdfOutputStepContracts.RenderPdfBlock, PendingActivityState.Scheduled))));
        Assert.Throws<ArgumentNullException>(() => PendingDispatchMapper.AnyWaiting(null!));
    }

    [Fact]
    public async Task ASampleSeesTheOutputsWaitingWorkAndTheJobsClass()
    {
        string? described = null;
        var (store, _, _) = Create(JobPriority.Low);
        var activities = new PdfPendingDispatchActivities(store, (id, _) =>
        {
            described = id;
            return Task.FromResult(Describe((PdfOutputStepContracts.RenderPdfBlock, PendingActivityState.Scheduled)));
        });

        var sample = await activities.ReadPendingDispatchAsync(new ReadPendingDispatchInput(JobId, "the-output-execution"));

        Assert.True(sample.Pending);
        Assert.Equal(JobPriority.Low, sample.JobClass);
        Assert.Equal("the-output-execution", described);
    }

    [Fact]
    public async Task ThePendingSampleRefusesNoInput()
    {
        var (store, _, _) = Create(JobPriority.Low);
        var pending = new PdfPendingDispatchActivities(store, (_, _) => Task.FromResult(new DescribeWorkflowExecutionResponse()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => pending.ReadPendingDispatchAsync(null!));
    }

    private static DescribeWorkflowExecutionResponse Describe(params (string Type, PendingActivityState State)[] activities)
    {
        var response = new DescribeWorkflowExecutionResponse();
        foreach (var (type, state) in activities)
        {
            response.PendingActivities.Add(new PendingActivityInfo
            {
                ActivityType = new Temporalio.Api.Common.V1.ActivityType { Name = type },
                State = state,
            });
        }

        return response;
    }

    private static (InMemoryJobStore Store, JobDispatchActivities Activities, List<(long Value, KeyValuePair<string, object?>[] Tags)> Promotions) Create(
        JobPriority priority)
    {
        var store = new InMemoryJobStore(new InMemoryUploadStore());
        store.SeedAccepted(JobId, OutputId, new TemplateReferenceRecord(JobId, "template", TemplateReferenceForm.Exact));
        store.PlaceInClass(JobId, priority);

        var identity = MeterIdentity.ForHost("ReportService.Tests.Promotions." + Guid.NewGuid().ToString("N"));
        var promotions = new List<(long, KeyValuePair<string, object?>[])>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == identity.Name && instrument.Name == OrchestrationMetrics.PriorityPromotionsInstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => promotions.Add((value, tags.ToArray())));
        listener.Start();

        var activities = new JobDispatchActivities(
            store,
            Options.Create(new PriorityAgingOptions { Wait = TimeSpan.FromSeconds(20) }),
            new OrchestrationMetrics(identity),
            NullLogger<JobDispatchActivities>.Instance);
        return (store, activities, promotions);
    }
}
