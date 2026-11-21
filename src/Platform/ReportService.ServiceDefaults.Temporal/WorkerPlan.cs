using Temporalio.Worker;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>A worker process: its name and the task queues it declares.</summary>
/// <param name="Name">The process name, as it appears in logs.</param>
/// <param name="DeclaredQueues">The task queues this process hosts, in order.</param>
public sealed record WorkflowEngineProcess(string Name, IReadOnlyList<string> DeclaredQueues);

/// <summary>
/// Work registered on a declared task queue: <see cref="Configure"/> adds
/// workflows and activities to the queue's worker options.
/// </summary>
/// <param name="Queue">The declared task queue the work runs on.</param>
/// <param name="Configure">Adds the workflows and activities.</param>
public sealed record TaskQueueWork(string Queue, Action<TemporalWorkerOptions> Configure);

/// <summary>One engine worker to create: its queue and its options with every registration applied.</summary>
public sealed record PlannedWorker(string Queue, TemporalWorkerOptions Options);

/// <summary>
/// Which engine workers a process creates: exactly one per declared task
/// queue that has registered work, in declaration order, and none for a
/// queue without work (the engine refuses a worker with nothing registered).
/// </summary>
public static class WorkerPlan
{
    /// <summary>Plans the workers; work on an undeclared queue is a <see cref="ConfigurationError"/> naming the queue.</summary>
    public static IReadOnlyList<PlannedWorker> Plan(IReadOnlyList<string> declaredQueues, IEnumerable<TaskQueueWork> registrations)
    {
        ArgumentNullException.ThrowIfNull(declaredQueues);
        ArgumentNullException.ThrowIfNull(registrations);
        var byQueue = registrations.GroupBy(r => r.Queue, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var undeclared = byQueue.Keys.Where(q => !declaredQueues.Contains(q, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (undeclared.Count > 0)
        {
            throw new ConfigurationError(
                [.. undeclared.Select(q => new ConfigurationProblem($"task queue '{q}'", "work is registered on a task queue this process does not declare"))]);
        }

        var plan = new List<PlannedWorker>();
        foreach (var queue in declaredQueues)
        {
            if (byQueue.TryGetValue(queue, out var work))
            {
                var options = new TemporalWorkerOptions(queue);
                foreach (var registration in work)
                {
                    registration.Configure(options);
                }

                plan.Add(new PlannedWorker(queue, options));
            }
        }

        return plan;
    }
}
