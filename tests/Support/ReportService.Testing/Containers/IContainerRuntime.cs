namespace ReportService.Testing.Containers;

/// <summary>
/// The container-runtime surface a test uses to observe and drive one container. A
/// caller addresses a container by name, the way the runtime's own CLI does; an
/// implementation acts on the exact name it is given and never substitutes another.
/// </summary>
public interface IContainerRuntime
{
    /// <summary>Whether the container with <paramref name="containerName"/> is running.</summary>
    /// <param name="containerName">The exact name of the container to inspect.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> while the container runs, <see langword="false"/> otherwise.</returns>
    /// <exception cref="ContainerMissingException">The runtime holds no container with that name.</exception>
    Task<bool> IsRunningAsync(string containerName, CancellationToken cancellationToken);

    /// <summary>
    /// The number of times the runtime has restarted the container with
    /// <paramref name="containerName"/>.
    /// </summary>
    /// <param name="containerName">The exact name of the container to inspect.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The restart count the runtime reports.</returns>
    /// <exception cref="ContainerMissingException">The runtime holds no container with that name.</exception>
    Task<int> RestartCountAsync(string containerName, CancellationToken cancellationToken);

    /// <summary>Starts the container with <paramref name="containerName"/>.</summary>
    /// <param name="containerName">The exact name of the container to start.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="ContainerMissingException">The runtime holds no container with that name.</exception>
    Task StartAsync(string containerName, CancellationToken cancellationToken);
}
