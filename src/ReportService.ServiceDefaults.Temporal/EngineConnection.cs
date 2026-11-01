using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>One connected engine client and the two calls readiness needs.</summary>
public interface IEngineConnection
{
    /// <summary>The connected client; engine workers are created from it.</summary>
    ITemporalClient Client { get; }

    /// <summary>Asks the engine whether it is serving; <c>false</c> when it does not answer within <paramref name="timeout"/> or the call fails.</summary>
    Task<bool> CheckHealthAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>The engine's server version.</summary>
    Task<string> ServerVersionAsync(CancellationToken cancellationToken);
}

/// <summary>Connects to the engine; throws when the engine cannot be reached.</summary>
public delegate Task<IEngineConnection> EngineConnector(WorkflowEngineOptions options, CancellationToken cancellationToken);

/// <summary>Waits; the host never reads the clock or sleeps any other way.</summary>
public delegate Task EngineDelay(TimeSpan wait, CancellationToken cancellationToken);

/// <summary>The connection of a <see cref="TemporalClient"/>, connected eagerly (never lazily).</summary>
public sealed class TemporalEngineConnection(ITemporalClient client) : IEngineConnection, IDisposable
{
    /// <inheritdoc />
    public ITemporalClient Client { get; } = client;

    /// <summary>Connects to the engine named by <paramref name="options"/>.</summary>
    public static async Task<IEngineConnection> ConnectAsync(WorkflowEngineOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = await TemporalClient
            .ConnectAsync(new TemporalClientConnectOptions(options.Address!) { Namespace = options.Namespace })
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return new TemporalEngineConnection(client);
    }

    /// <inheritdoc />
    public async Task<bool> CheckHealthAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await Client.Connection
                .CheckHealthAsync(options: new RpcOptions { Timeout = timeout, CancellationToken = cancellationToken })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string> ServerVersionAsync(CancellationToken cancellationToken)
    {
        var info = await Client.WorkflowService
            .GetSystemInfoAsync(new GetSystemInfoRequest(), new RpcOptions { CancellationToken = cancellationToken })
            .ConfigureAwait(false);
        return info.ServerVersion;
    }

    /// <inheritdoc />
    public void Dispose() => (Client.Connection as IDisposable)?.Dispose();
}
