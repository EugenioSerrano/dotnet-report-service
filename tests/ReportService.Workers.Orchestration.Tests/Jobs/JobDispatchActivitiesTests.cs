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
        await Assert.ThrowsAsync<ArgumentNullException>(() => activities.PromoteToNormalAsync(null!));
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
