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
/// The dispatch steps over the in-memory store and a built engine description: the class and
/// the wait read once; a sample sees render or assembly work scheduled and not started and the
/// job's current class; a promotion moves low to normal once and counts once.
/// </summary>
public sealed class JobDispatchActivitiesTests
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
    public async Task TheDispatchReadsTheJobsClassAndTheDeploymentsWait()
    {
        var (store, activities, _) = Create(JobPriority.Low);

        var dispatch = await new ActivityEnvironment().RunAsync(() => activities.ReadJobDispatchAsync(new ReadJobDispatchInput(JobId)));

        Assert.Equal(JobPriority.Low, dispatch.PriorityClass);
        Assert.Equal(20, dispatch.AgingWaitSeconds);
        Assert.Equal(JobPriority.Normal, (await activities.ReadJobDispatchAsync(new ReadJobDispatchInput("job-missing"))).PriorityClass);
        Assert.Equal(0, store.Promotions);
    }

    [Fact]
    public async Task ASampleSeesTheOutputsWaitingWorkAndTheJobsClass()
    {
        string? described = null;
        var (_, activities, _) = Create(JobPriority.Low, (id, _) =>
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
    public async Task APromotionMovesLowToNormalOnceAndCountsOnce()
    {
        var (store, activities, promotions) = Create(JobPriority.Low);

        Assert.True(await activities.PromoteToNormalAsync(new PromoteToNormalInput(JobId, 600)));
        Assert.False(await activities.PromoteToNormalAsync(new PromoteToNormalInput(JobId, 630)));

        Assert.Equal(JobPriority.Normal, await store.ReadEffectiveClassAsync(JobId, CancellationToken.None));
        Assert.Equal(1, store.Promotions);
        var counted = Assert.Single(promotions);
        Assert.Equal("low", counted.Tags.Single(tag => tag.Key == "from").Value);
        Assert.Equal("normal", counted.Tags.Single(tag => tag.Key == "to").Value);
    }

    [Theory]
    [InlineData(JobPriority.Normal)]
    [InlineData(JobPriority.High)]
    public async Task AJobThatIsNotLowIsNeverPromotedOrCounted(JobPriority priority)
    {
        var (store, activities, promotions) = Create(priority);

        Assert.False(await activities.PromoteToNormalAsync(new PromoteToNormalInput(JobId, 600)));
        Assert.Equal(priority, await store.ReadEffectiveClassAsync(JobId, CancellationToken.None));
        Assert.Empty(promotions);
    }

    [Fact]
    public async Task APromotionOfAJobThatDoesNotExistChangesNothing()
    {
        var (_, activities, promotions) = Create(JobPriority.Low);
        Assert.False(await activities.PromoteToNormalAsync(new PromoteToNormalInput("job-missing", 600)));
        Assert.Empty(promotions);
    }

    [Fact]
    public async Task TheStepsRefuseNoInput()
    {
        var (_, activities, _) = Create(JobPriority.Low);
        await Assert.ThrowsAsync<ArgumentNullException>(() => activities.ReadJobDispatchAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => activities.ReadPendingDispatchAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => activities.PromoteToNormalAsync(null!));
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
        JobPriority priority,
        Func<string, CancellationToken, Task<DescribeWorkflowExecutionResponse>>? describe = null)
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
            describe ?? ((_, _) => Task.FromResult(new DescribeWorkflowExecutionResponse())),
            new OrchestrationMetrics(identity),
            NullLogger<JobDispatchActivities>.Instance);
        return (store, activities, promotions);
    }
}
