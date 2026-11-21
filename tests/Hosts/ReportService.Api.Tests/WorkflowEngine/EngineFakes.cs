using ReportService.ServiceDefaults.Temporal;
using Temporalio.Activities;
using Temporalio.Client;

namespace ReportService.Api.Tests.WorkflowEngine;

/// <summary>A connection that answers its probe from a flag; it has no client.</summary>
internal sealed class FakeConnection(string name) : IEngineConnection, IDisposable
{
    public string Name { get; } = name;

    public bool Healthy { get; set; } = true;

    public bool Disposed { get; private set; }

    public ITemporalClient Client => throw new InvalidOperationException("A fake connection has no client.");

    public Func<CancellationToken, Task<bool>>? Probe { get; set; }

    public Task<bool> CheckHealthAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Probe is null ? Task.FromResult(Healthy) : Probe(cancellationToken);

    public Task<string> ServerVersionAsync(CancellationToken cancellationToken) => Task.FromResult("test-server");

    public void Dispose() => Disposed = true;
}

/// <summary>A connector that plays a script: each step either fails or returns a connection.</summary>
internal sealed class ScriptedConnector(params Func<IEngineConnection>[] steps)
{
    private int _calls;

    public int Calls => _calls;

    public EngineConnector Connector => (_, _) =>
    {
        var index = _calls++;
        if (index >= steps.Length)
        {
            throw new InvalidOperationException("The connector script has no more steps.");
        }

        return Task.FromResult(steps[index]());
    };

    public static Func<IEngineConnection> Refused(string message = "Connection failed: refused") =>
        () => throw new InvalidOperationException(message);

    public static Func<IEngineConnection> Returns(IEngineConnection connection) => () => connection;
}

/// <summary>
/// A delay that returns at once and records each requested wait. The callback of
/// each call may change the test's state; after <c>stopAfter</c> calls it cancels
/// the run, so the host's loop always ends.
/// </summary>
internal sealed class RecordingDelay(CancellationTokenSource run, int stopAfter, Action<int>? onDelay = null)
{
    public List<TimeSpan> Waits { get; } = [];

    public EngineDelay Delay => (wait, cancellationToken) =>
    {
        Waits.Add(wait);
        onDelay?.Invoke(Waits.Count);
        if (Waits.Count >= stopAfter)
        {
            run.Cancel();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    };
}

/// <summary>An engine worker that runs until it is cancelled or told to stop.</summary>
internal sealed class FakeWorker(PlannedWorker plan) : IRunningEngineWorker
{
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlannedWorker Plan { get; } = plan;

    public List<IEngineConnection> ClientsReceived { get; } = [];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => _stopped.TrySetCanceled(cancellationToken));
        await _stopped.Task;
    }

    public void UseConnection(IEngineConnection connection) => ClientsReceived.Add(connection);

    public void Stop() => _stopped.TrySetResult();
}

/// <summary>Activities used only to register work in tests; never executed.</summary>
internal static class SampleActivities
{
    [Activity]
    public static string First() => "first";

    [Activity]
    public static string Second() => "second";
}
