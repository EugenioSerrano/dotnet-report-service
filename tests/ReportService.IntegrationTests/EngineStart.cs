using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Containers;
using ReportService.Testing;
using Temporalio.Client;

namespace ReportService.IntegrationTests;

/// <summary>
/// Starts the workflow engine every integration class runs against: one explicit
/// host port per engine, the engine's own gRPC health as the readiness gate, and a
/// bounded retry when the port is taken between the probe and Docker's bind.
/// </summary>
public static class EngineStart
{
    /// <summary>The engine's gRPC port inside the container.</summary>
    public const int GrpcPort = 7233;

    /// <summary>How long the engine may take to answer its own health after a start.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    /// <summary>How long one health probe may take before it is abandoned.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How many starts a bind conflict may cost before the failure surfaces.</summary>
    public const int StartAttempts = 3;

    /// <summary>
    /// A started engine: the container that carries it, the host port it is bound
    /// to, and the ports each start attempt used, in order.
    /// </summary>
    public sealed record Engine(IContainer Container, int HostPort, IReadOnlyList<int> AttemptedPorts);

    /// <summary>
    /// Chooses a host port, starts the engine's container on it and waits for the
    /// engine's own gRPC health. A start that meets a bind conflict re-picks the
    /// port and retries, at most <see cref="StartAttempts"/> times; any other start
    /// failure, and a failure of the last attempt, surfaces with the ports tried.
    /// </summary>
    public static async Task<Engine> StartAsync(PinnedImage image, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var attemptedPorts = new List<int>(StartAttempts);
        Exception? lastConflict = null;
        for (var attempt = 1; attempt <= StartAttempts; attempt++)
        {
            var hostPort = FreePort();
            attemptedPorts.Add(hostPort);
            var container = Create(image, role, hostPort);
            try
            {
                await ContainerSmoke.StartAsync(container, image, Ready(hostPort, image), cancellationToken)
                    .ConfigureAwait(false);
                return EngineFor(container, image, hostPort, [.. attemptedPorts]);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (!IsBindConflict(exception))
                {
                    throw;
                }

                lastConflict = exception;
            }
        }

        throw new InvalidOperationException(
            $"The workflow engine ({image.Reference}) could not bind a host port in {StartAttempts} attempts " +
            $"(ports: {string.Join(", ", attemptedPorts)}): {lastConflict!.Message}",
            lastConflict);
    }

    /// <summary>
    /// Chooses a host port and builds the engine's container without starting it,
    /// for a case that must build its host against the address before anything
    /// serves. Nothing exists in Docker until a start, so a caller that fails
    /// before <see cref="StartPreparedAsync"/> leaves nothing behind.
    /// </summary>
    public static Engine Prepare(PinnedImage image, string role)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var hostPort = FreePort();
        return new Engine(Create(image, role, hostPort), hostPort, [hostPort]);
    }

    /// <summary>
    /// Starts an engine built by <see cref="Prepare"/>, through the same readiness
    /// overload of <see cref="ContainerSmoke.StartAsync"/> a fresh start uses, so a
    /// start or readiness failure removes the container. The port is not re-picked:
    /// nothing may be re-bound once a host already holds the address.
    /// </summary>
    public static async Task StartPreparedAsync(Engine engine, PinnedImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(image);
        await ContainerSmoke.StartAsync(engine.Container, image, Ready(engine.HostPort, image), cancellationToken)
            .ConfigureAwait(false);
        AssertMappedPort(engine.Container, image, engine.HostPort);
    }

    /// <summary>
    /// Starts the same container again and waits for the engine's own health; the
    /// explicit binding keeps the host port across a restart. The readiness gate of
    /// this path removes nothing: the class already holds the container and releases
    /// it through its own teardown when the case fails.
    /// </summary>
    public static async Task RestartAsync(Engine engine, PinnedImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(image);
        await engine.Container.StartAsync(cancellationToken).ConfigureAwait(false);
        await Ready(engine.HostPort, image)(cancellationToken).ConfigureAwait(false);
        AssertMappedPort(engine.Container, image, engine.HostPort);
    }

    private static IContainer Create(PinnedImage image, string role, int hostPort) =>
        ContainerSmoke.Builder(image, role)
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(hostPort, GrpcPort)
            .Build();

    private static Func<CancellationToken, Task> Ready(int hostPort, PinnedImage image)
    {
        var connection = TemporalConnection.CreateLazy(new TemporalConnectionOptions($"127.0.0.1:{hostPort}"));
        return token => Wait.UntilAsync(
            $"the workflow engine ({image.Reference}) answering its own gRPC health on 127.0.0.1:{hostPort}",
            probeToken => connection.CheckHealthAsync(options: new RpcOptions { CancellationToken = probeToken }),
            Deadline,
            TimeSpan.FromMilliseconds(500),
            AttemptTimeout,
            token);
    }

    private static Engine EngineFor(
        IContainer container,
        PinnedImage image,
        int hostPort,
        IReadOnlyList<int> attemptedPorts)
    {
        AssertMappedPort(container, image, hostPort);
        return new Engine(container, hostPort, attemptedPorts);
    }

    private static void AssertMappedPort(IContainer container, PinnedImage image, int hostPort)
    {
        var mapped = container.GetMappedPublicPort(GrpcPort);
        if (mapped != hostPort)
        {
            throw new InvalidOperationException(
                $"The workflow engine ({image.Reference}) was bound to host port {hostPort}, " +
                $"but its container maps port {mapped} to gRPC port {GrpcPort}.");
        }
    }

    private static bool IsBindConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("failed to bind host port", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
