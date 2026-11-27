using ReportService.Testing.Containers;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// An in-memory container runtime modelling one daemon with several compositions'
/// containers: every name carries its running state and restart count, and every
/// inspected or started name is recorded, so a test can prove which containers a
/// caller addressed. There is no way to ask it which containers exist: a caller can
/// only act on a name it already holds.
/// </summary>
public sealed class StubContainerRuntime : IContainerRuntime
{
    private readonly Dictionary<string, Entry> _containers = new(StringComparer.Ordinal);
    private readonly List<string> _inspections = [];
    private readonly List<string> _starts = [];

    /// <summary>The names inspected, in the order they were inspected.</summary>
    public IReadOnlyList<string> InspectedNames => _inspections;

    /// <summary>The names started, in the order they were started.</summary>
    public IReadOnlyList<string> StartedNames => _starts;

    /// <summary>Adds a container to the daemon.</summary>
    /// <param name="name">The container's exact name.</param>
    /// <param name="running">Whether the container starts out running.</param>
    /// <param name="restartCount">The restart count the runtime reports for it.</param>
    /// <returns>This stub, so a test can build a daemon in one expression.</returns>
    public StubContainerRuntime Add(string name, bool running = true, int restartCount = 0)
    {
        _containers[name] = new Entry { Running = running, RestartCount = restartCount };
        return this;
    }

    /// <summary>Removes a container, as disposing the composition that owned it does.</summary>
    /// <param name="name">The container's exact name.</param>
    public void Remove(string name) => _containers.Remove(name);

    /// <summary>Sets whether a container is running, as the runtime's own state changes.</summary>
    /// <param name="name">The container's exact name.</param>
    /// <param name="running">The state the container moves to.</param>
    public void SetRunning(string name, bool running) => StateOf(name).Running = running;

    /// <summary>Sets the restart count a container reports.</summary>
    /// <param name="name">The container's exact name.</param>
    /// <param name="restartCount">The count the container moves to.</param>
    public void SetRestartCount(string name, int restartCount) => StateOf(name).RestartCount = restartCount;

    /// <inheritdoc />
    public Task<bool> IsRunningAsync(string containerName, CancellationToken cancellationToken)
    {
        _inspections.Add(containerName);
        return Task.FromResult(StateOf(containerName).Running);
    }

    /// <inheritdoc />
    public Task<int> RestartCountAsync(string containerName, CancellationToken cancellationToken)
    {
        _inspections.Add(containerName);
        return Task.FromResult(StateOf(containerName).RestartCount);
    }

    /// <inheritdoc />
    public Task StartAsync(string containerName, CancellationToken cancellationToken)
    {
        _starts.Add(containerName);
        StateOf(containerName).Running = true;
        return Task.CompletedTask;
    }

    private Entry StateOf(string name) =>
        _containers.TryGetValue(name, out var entry)
            ? entry
            : throw new ContainerMissingException(name, $"error: no such object: {name}");

    private sealed class Entry
    {
        public bool Running { get; set; }

        public int RestartCount { get; set; }
    }
}
