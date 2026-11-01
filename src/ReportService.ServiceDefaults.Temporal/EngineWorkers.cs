using System.Collections.Concurrent;
using Temporalio.Worker;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>A running engine worker on one task queue.</summary>
public interface IRunningEngineWorker
{
    /// <summary>Polls its task queue until cancelled or until it fails.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Switches the worker to a new connection without restarting it.</summary>
    void UseConnection(IEngineConnection connection);
}

/// <summary>Creates the engine worker of one planned queue on a connected client.</summary>
public delegate IRunningEngineWorker EngineWorkerFactory(IEngineConnection connection, PlannedWorker plan);

/// <summary>An engine worker of the workflow-engine SDK.</summary>
public sealed class TemporalEngineWorker(IEngineConnection connection, PlannedWorker plan) : IRunningEngineWorker
{
    private readonly TemporalWorker _worker = new(connection.Client, plan.Options);

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using (_worker)
        {
            await _worker.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void UseConnection(IEngineConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _worker.Client = connection.Client;
    }
}

/// <summary>
/// The state of each declared task queue: no registered work, its worker
/// running, or its worker stopped. Readiness reads it.
/// </summary>
public sealed class EngineWorkerStates
{
    /// <summary>A declared queue with no registered work: no worker exists.</summary>
    public const string NoWork = "no registered work";

    /// <summary>The queue's worker is running.</summary>
    public const string Running = "worker running";

    /// <summary>The queue's worker has ended.</summary>
    public const string Stopped = "worker stopped";

    private readonly IReadOnlyList<string> _declared;
    private readonly ConcurrentDictionary<string, string> _states;

    /// <summary>Every declared queue starts with <see cref="NoWork"/>.</summary>
    public EngineWorkerStates(IEnumerable<string> declaredQueues)
    {
        _declared = [.. declaredQueues];
        _states = new ConcurrentDictionary<string, string>(_declared.Select(q => KeyValuePair.Create(q, NoWork)), StringComparer.Ordinal);
    }

    /// <summary>Whether no created worker has stopped.</summary>
    public bool AllRunning => !_states.Values.Contains(Stopped);

    /// <summary>Sets the state of a declared queue.</summary>
    public void Set(string queue, string state)
    {
        if (!_states.ContainsKey(queue))
        {
            throw new ArgumentException($"The task queue '{queue}' is not declared by this process.", nameof(queue));
        }

        _states[queue] = state;
    }

    /// <summary>The state of every declared queue, in declaration order.</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var queue in _declared)
        {
            snapshot[queue] = _states[queue];
        }

        return snapshot;
    }
}
