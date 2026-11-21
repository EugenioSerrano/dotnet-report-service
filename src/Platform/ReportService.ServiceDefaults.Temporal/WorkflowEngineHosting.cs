using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Temporalio.Worker;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>Registration of the workflow-engine connection in a worker process.</summary>
public static class WorkflowEngineHosting
{
    /// <summary>
    /// Connects this process to the workflow engine (<c>Temporal:Address</c>,
    /// <c>Temporal:Namespace</c>, validated at start), declares its task queues
    /// and adds the <see cref="TemporalConnectionHealthCheck"/>. An engine worker
    /// is created only for a declared queue that has work registered with
    /// <see cref="AddTaskQueueWork"/>.
    /// </summary>
    public static IHostApplicationBuilder AddWorkflowEngine(this IHostApplicationBuilder builder, string processName, IReadOnlyList<string> declaredQueues)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentNullException.ThrowIfNull(declaredQueues);

        builder.AddValidatedConfiguration<WorkflowEngineOptions>(WorkflowEngineOptions.SectionPath);
        var services = builder.Services;
        services.AddSingleton(new WorkflowEngineProcess(processName, declaredQueues));
        services.AddSingleton<TemporalClientHolder>();
        services.AddSingleton(new EngineWorkerStates(declaredQueues));
        services.AddSingleton<EngineConnector>(TemporalEngineConnection.ConnectAsync);
        services.AddSingleton<EngineDelay>((wait, cancellationToken) => Task.Delay(wait, TimeProvider.System, cancellationToken));
        services.AddSingleton<EngineWorkerFactory>((connection, plan) => new TemporalEngineWorker(connection, plan));
        services.AddSingleton<TemporalWorkerHost>();
        services.AddHostedService(provider => provider.GetRequiredService<TemporalWorkerHost>());
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            TemporalConnectionHealthCheck.Name,
            provider => new TemporalConnectionHealthCheck(
                provider.GetRequiredService<TemporalClientHolder>(),
                provider.GetRequiredService<EngineWorkerStates>(),
                provider.GetRequiredService<IOptions<WorkflowEngineOptions>>(),
                TemporalConnectionHealthCheck.DefaultTimeout),
            failureStatus: null,
            tags: null));
        return builder;
    }

    /// <summary>Registers work (workflows, activities) on one of the process's declared task queues.</summary>
    public static IServiceCollection AddTaskQueueWork(this IServiceCollection services, string queue, Action<TemporalWorkerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddSingleton(new TaskQueueWork(queue, configure));
    }
}
