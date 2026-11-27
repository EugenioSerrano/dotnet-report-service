namespace ReportService.Testing.Containers;

/// <summary>
/// The restart harness's handle on the merge worker it resolves: the container's
/// resolved name and the observations and actions the interruption window performs.
/// The handle addresses one container by name; it never searches the runtime itself.
/// </summary>
public sealed class MergeWorkerContainer
{
    private readonly IContainerRuntime _runtime;

    private MergeWorkerContainer(IContainerRuntime runtime, string name)
    {
        _runtime = runtime;
        Name = name;
    }

    /// <summary>The container's name, as the composition declared it.</summary>
    public string Name { get; }

    /// <summary>
    /// Resolves the merge worker container of the composition that declared
    /// <paramref name="declaredName"/>: the declared name is the resolution. The
    /// container is inspected once to prove the composition's own container exists;
    /// a name the runtime does not hold fails loudly with that name, and no other
    /// container is ever considered.
    /// </summary>
    /// <param name="runtime">The container runtime to resolve against.</param>
    /// <param name="declaredName">The composition's declared merge-worker container name.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The handle the window drives.</returns>
    /// <exception cref="InvalidOperationException">The runtime holds no container with the declared name.</exception>
    public static async Task<MergeWorkerContainer> ResolveAsync(
        IContainerRuntime runtime,
        string declaredName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredName);
        try
        {
            await runtime.IsRunningAsync(declaredName, cancellationToken);
        }
        catch (ContainerMissingException exception)
        {
            throw new InvalidOperationException(
                $"the composition's merge worker container '{declaredName}' does not exist",
                exception);
        }

        return new MergeWorkerContainer(runtime, declaredName);
    }

    /// <summary>Whether this container is running.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="ContainerMissingException">The runtime holds no container with this name.</exception>
    public Task<bool> IsRunningAsync(CancellationToken cancellationToken) =>
        _runtime.IsRunningAsync(Name, cancellationToken);

    /// <summary>This container's restart count, as the runtime reports it.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="ContainerMissingException">The runtime holds no container with this name.</exception>
    public Task<int> RestartCountAsync(CancellationToken cancellationToken) =>
        _runtime.RestartCountAsync(Name, cancellationToken);

    /// <summary>Starts this container.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="ContainerMissingException">The runtime holds no container with this name.</exception>
    public Task StartAsync(CancellationToken cancellationToken) =>
        _runtime.StartAsync(Name, cancellationToken);

    /// <summary>
    /// Waits until this container is not running, or its restart count has moved above
    /// <paramref name="restartsBefore"/>: the runtime restarted the container, or it
    /// stopped and the caller starts it. The failure names <paramref name="what"/> and
    /// carries the last error, exactly as the bounded wait does.
    /// </summary>
    /// <param name="what">What the wait is for, as its failure message names it.</param>
    /// <param name="restartsBefore">The container's restart count when the window opened.</param>
    /// <param name="deadline">How long the wait may take.</param>
    /// <param name="interval">How often the container is inspected.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public Task WaitUntilStoppedOrRestartedAsync(
        string what,
        int restartsBefore,
        TimeSpan deadline,
        TimeSpan interval,
        CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            what,
            async token => !await IsRunningAsync(token) || await RestartCountAsync(token) > restartsBefore,
            deadline,
            interval,
            cancellationToken);

    /// <summary>
    /// Waits until this container is running again. The failure names
    /// <paramref name="what"/> and carries the last error, exactly as the bounded wait
    /// does.
    /// </summary>
    /// <param name="what">What the wait is for, as its failure message names it.</param>
    /// <param name="deadline">How long the wait may take.</param>
    /// <param name="interval">How often the container is inspected.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public Task WaitUntilRunningAsync(
        string what,
        TimeSpan deadline,
        TimeSpan interval,
        CancellationToken cancellationToken) =>
        Wait.UntilAsync(what, token => IsRunningAsync(token), deadline, interval, cancellationToken);
}
