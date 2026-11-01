using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;

namespace ReportService.Api.Tests.WorkflowEngine;

/// <summary>One engine worker per declared task queue that has registered work, and none without work.</summary>
public sealed class WorkerPlanTests
{
    private static readonly string[] Declared = ["report-orchestration", "report-data"];

    [Fact]
    public void NoRegistrationPlansNoWorker()
    {
        Assert.Empty(WorkerPlan.Plan(Declared, []));
    }

    [Fact]
    public void TwoRegistrationsOnOneQueuePlanOneWorkerCarryingBoth()
    {
        var plan = WorkerPlan.Plan(Declared,
        [
            new TaskQueueWork("report-data", o => o.AddActivity(SampleActivities.First)),
            new TaskQueueWork("report-data", o => o.AddActivity(SampleActivities.Second)),
        ]);

        var worker = Assert.Single(plan);
        Assert.Equal("report-data", worker.Queue);
        Assert.Equal("report-data", worker.Options.TaskQueue);
        Assert.Equal(2, worker.Options.Activities.Count);
    }

    [Fact]
    public void RegistrationsOnTwoQueuesPlanTwoWorkersInDeclarationOrder()
    {
        var plan = WorkerPlan.Plan(Declared,
        [
            new TaskQueueWork("report-data", o => o.AddActivity(SampleActivities.First)),
            new TaskQueueWork("report-orchestration", o => o.AddActivity(SampleActivities.Second)),
        ]);

        Assert.Equal(["report-orchestration", "report-data"], plan.Select(p => p.Queue));
    }

    [Fact]
    public void RegistrationOnAnUndeclaredQueueIsRefusedNamingTheQueue()
    {
        var error = Assert.Throws<ConfigurationError>(() => WorkerPlan.Plan(Declared,
        [
            new TaskQueueWork("report-pdf-rendering", o => o.AddActivity(SampleActivities.First)),
        ]));

        Assert.Contains("report-pdf-rendering", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueNamesAreComparedExactly()
    {
        Assert.Throws<ConfigurationError>(() => WorkerPlan.Plan(Declared,
        [
            new TaskQueueWork("Report-Data", o => o.AddActivity(SampleActivities.First)),
        ]));
    }
}
