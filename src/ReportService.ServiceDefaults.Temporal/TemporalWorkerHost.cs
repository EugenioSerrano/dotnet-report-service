using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>
/// Connects the process to the workflow engine and keeps it connected:
/// <list type="number">
/// <item>connects first, retrying with a backoff of 2 s doubling to 60 s, without ever exiting;</item>
/// <item>only then creates one engine worker per declared queue that has registered work (none when no work is registered);</item>
/// <item>probes the connection every 5 s; when it is lost, reconnects with the same backoff and replaces it, handing the new client to the running workers without restarting them.</item>
/// </list>
/// Readiness is not this service's state but <see cref="TemporalConnectionHealthCheck"/>.
/// </summary>
public sealed partial class TemporalWorkerHost : BackgroundService
{
    /// <summary>How often the connection is probed once connected.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long one probe may take.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(60);

    private readonly WorkflowEngineProcess _process;
    private readonly WorkflowEngineOptions _options;
    private readonly TemporalClientHolder _holder;
    private readonly EngineWorkerStates _states;
    private readonly IReadOnlyList<TaskQueueWork> _work;
    private readonly EngineConnector _connector;
    private readonly EngineDelay _delay;
    private readonly EngineWorkerFactory _workerFactory;
    private readonly ILogger<TemporalWorkerHost> _logger;
    private IReadOnlyList<PlannedWorker>? _plan;

    /// <summary>Creates the host; the connector, the delay and the worker factory are injected so tests need no engine and no clock.</summary>
    public TemporalWorkerHost(
        WorkflowEngineProcess process,
        IOptions<WorkflowEngineOptions> options,
        TemporalClientHolder holder,
        EngineWorkerStates states,
        IEnumerable<TaskQueueWork> work,
        EngineConnector connector,
        EngineDelay delay,
        EngineWorkerFactory workerFactory,
        ILogger<TemporalWorkerHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _process = process;
        _options = options.Value;
        _holder = holder;
        _states = states;
        _work = [.. work];
        _connector = connector;
        _delay = delay;
        _workerFactory = workerFactory;
        _logger = logger;
    }

    /// <summary>The wait after the <paramref name="failedAttempts"/>-th failed connection: 2 s, doubling, at most 60 s.</summary>
    public static TimeSpan BackoffDelay(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        var wait = FirstBackoff;
        for (var attempt = 1; attempt < failedAttempts && wait < MaximumBackoff; attempt++)
        {
            wait *= 2;
        }

        return wait < MaximumBackoff ? wait : MaximumBackoff;
    }

    /// <summary>Checks the registered work before anything starts: work on an undeclared queue stops start-up.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _plan = WorkerPlan.Plan(_process.DeclaredQueues, _work);
        return base.StartAsync(cancellationToken);
    }

    /// <summary>Connects, creates the planned workers and keeps the connection; ends only by cancellation.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var plan = _plan ?? WorkerPlan.Plan(_process.DeclaredQueues, _work);
        LogStarting(_process.Name, string.Join(", ", _process.DeclaredQueues), _options.Address, _options.Namespace, ConfigurationValidation.ProductVersion());

        var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        _holder.Replace(connection);
        LogConnected(_options.Address, _options.Namespace, await ServerVersionAsync(connection, cancellationToken).ConfigureAwait(false));

        foreach (var queue in _process.DeclaredQueues.Where(q => plan.All(p => p.Queue != q)))
        {
            LogNoWork(queue);
        }

        var workers = plan.Select(p => (p.Queue, Worker: _workerFactory(connection, p))).ToList();
        var runs = workers.Select(w => RunWorkerAsync(w.Queue, w.Worker, cancellationToken)).ToList();
        try
        {
            while (true)
            {
                await _delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
                if (await ProbeAsync(_holder.Current!, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                LogLost(_options.Address);
                var fresh = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                var previous = _holder.Replace(fresh);
                foreach (var (_, worker) in workers)
                {
                    worker.UseConnection(fresh);
                }

                (previous as IDisposable)?.Dispose();
                LogRecovered(_options.Address, await ServerVersionAsync(fresh, cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            await Task.WhenAll(runs).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    private async Task<IEngineConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        for (var failed = 0; ;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await _connector(_options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                var wait = BackoffDelay(failed);
                LogConnectFailed(_options.Address, failed, exception.Message, wait.TotalSeconds);
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> ProbeAsync(IEngineConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.CheckHealthAsync(ProbeTimeout, cancellationToken).WaitAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<string> ServerVersionAsync(IEngineConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.ServerVersionAsync(cancellationToken).WaitAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "unknown";
        }
    }

    private async Task RunWorkerAsync(string queue, IRunningEngineWorker worker, CancellationToken cancellationToken)
    {
        _states.Set(queue, EngineWorkerStates.Running);
        try
        {
            await worker.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogWorkerFailed(exception, queue);
        }
        finally
        {
            _states.Set(queue, EngineWorkerStates.Stopped);
            if (!cancellationToken.IsCancellationRequested)
            {
                LogWorkerStopped(queue);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "starting {Process}: task queues {Queues}; workflow engine {Address}, namespace {Namespace}; version {Version}")]
    private partial void LogStarting(string process, string queues, string? address, string @namespace, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "connected to the workflow engine at {Address}, namespace {Namespace} (server {ServerVersion})")]
    private partial void LogConnected(string? address, string @namespace, string serverVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "task queue {Queue}: no registered work")]
    private partial void LogNoWork(string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "could not connect to the workflow engine at {Address} (attempt {Attempt}): {Reason}; next attempt in {WaitSeconds} s")]
    private partial void LogConnectFailed(string? address, int attempt, string reason, double waitSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "engine connection lost: the workflow engine at {Address} does not answer; reconnecting")]
    private partial void LogLost(string? address);

    [LoggerMessage(Level = LogLevel.Information, Message = "engine connection recovered: the workflow engine at {Address} answers again (server {ServerVersion})")]
    private partial void LogRecovered(string? address, string serverVersion);

    [LoggerMessage(Level = LogLevel.Error, Message = "the worker of task queue {Queue} failed")]
    private partial void LogWorkerFailed(Exception exception, string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the worker of task queue {Queue} stopped")]
    private partial void LogWorkerStopped(string queue);
}
