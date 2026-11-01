using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;

namespace ReportService.Api.Tests.WorkflowEngine;

/// <summary>Readiness is the engine answering on the process's own connection, and every created worker running.</summary>
public sealed class TemporalConnectionHealthCheckTests
{
    private static readonly string[] Declared = ["report-orchestration", "report-data"];

    private readonly TemporalClientHolder _holder = new();
    private readonly EngineWorkerStates _states = new(Declared);

    [Fact]
    public async Task NoConnectionYetIsUnhealthyConnecting()
    {
        var result = await Check();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("connecting", result.Description);
    }

    [Fact]
    public async Task AnsweringEngineIsHealthyWithAddressNamespaceAndQueues()
    {
        _holder.Replace(new FakeConnection("c"));

        var result = await Check();

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(["address", "namespace", "queues"], result.Data.Keys);
        Assert.Equal("localhost:7233", result.Data["address"]);
        Assert.Equal("default", result.Data["namespace"]);
        var queues = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(result.Data["queues"]);
        Assert.Equal("no registered work", queues["report-orchestration"]);
        Assert.Equal("no registered work", queues["report-data"]);
    }

    [Fact]
    public async Task ProbeAnsweringFalseIsUnhealthy()
    {
        _holder.Replace(new FakeConnection("c") { Healthy = false });

        var result = await Check();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("engine unreachable", result.Description);
    }

    [Fact]
    public async Task ProbeThrowingIsUnhealthy()
    {
        _holder.Replace(new FakeConnection("c") { Probe = _ => throw new InvalidOperationException("transport error") });

        var result = await Check();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("engine unreachable", result.Description);
    }

    [Fact]
    public async Task ProbeThatNeverAnswersIsUnhealthyAtTheTimeout()
    {
        _holder.Replace(new FakeConnection("c") { Probe = token => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => true, TaskScheduler.Default) });

        var result = await Check(TimeSpan.FromMilliseconds(50));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("engine unreachable", result.Description);
    }

    [Fact]
    public async Task StoppedWorkerMakesItUnhealthyNamingItsQueue()
    {
        _holder.Replace(new FakeConnection("c"));
        _states.Set("report-data", EngineWorkerStates.Stopped);

        var result = await Check();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("worker stopped: report-data", result.Description);
    }

    [Fact]
    public async Task RunningWorkerKeepsItHealthy()
    {
        _holder.Replace(new FakeConnection("c"));
        _states.Set("report-data", EngineWorkerStates.Running);

        var result = await Check();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void UnknownQueueStateIsRefused()
    {
        Assert.Throws<ArgumentException>(() => _states.Set("report-pdf-rendering", EngineWorkerStates.Running));
    }

    [Fact]
    public void ReplacingTheConnectionReturnsThePreviousOne()
    {
        var first = new FakeConnection("first");
        var second = new FakeConnection("second");

        Assert.Null(_holder.Replace(first));
        Assert.Same(first, _holder.Replace(second));
        Assert.Same(second, _holder.Current);
    }

    [Fact]
    public async Task RegisteredEngineReportsUnderItsNameWithoutAnyConnection()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Temporal:Address"] = "localhost:7233" });
        builder.AddServiceConfiguration();
        builder.AddWorkflowEngine("orchestration-worker", Declared);
        builder.Services.AddTaskQueueWork("report-data", o => o.AddActivity(SampleActivities.First));
        using var host = builder.Build();

        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);
        var registrations = host.Services.GetServices<TaskQueueWork>().ToList();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(TemporalConnectionHealthCheck.Name, entry.Key);
        Assert.Equal(HealthStatus.Unhealthy, entry.Value.Status);
        Assert.Single(registrations);
        Assert.Equal("default", host.Services.GetRequiredService<IOptions<WorkflowEngineOptions>>().Value.Namespace);
    }

    private Task<HealthCheckResult> Check(TimeSpan? timeout = null)
    {
        var check = new TemporalConnectionHealthCheck(
            _holder,
            _states,
            Microsoft.Extensions.Options.Options.Create(new WorkflowEngineOptions { Address = "localhost:7233", Namespace = "default" }),
            timeout ?? TemporalConnectionHealthCheck.DefaultTimeout);
        return check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
    }
}
