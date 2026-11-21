using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Jobs;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// The watchdog of a render that neither writes nor returns. A block activity registers
/// itself - its start, its start-to-close deadline and its output, block and attempt -
/// before the renderer is called. When the attempt is still registered past its own
/// deadline plus the grace, the watchdog acts once for that attempt: it logs and reports
/// the worker unhealthy through the delivered health surface, stops the attempt's beats,
/// records the watchdog's cause for the attempt, builds the typed failure the activity
/// raises when the engine still lets it, and ends the process. The platform replaces the
/// container; the engine sees the heartbeat timeout and retries the attempt on a healthy
/// worker. The cause row is what makes the mapping independent of the typed failure.
/// </summary>
internal sealed class RenderWatchdog : BackgroundService
{
    /// <summary>The exit code of a process the watchdog stopped.</summary>
    public const int ExitCode = 1;

    private readonly ConcurrentDictionary<AttemptRegistration, byte> _attempts = new();
    private readonly IJobStore _jobs;
    private readonly IProcessExit _exit;
    private readonly RenderWatchdogOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RenderWatchdog> _logger;
    private readonly PdfRenderingMetrics? _metrics;
    private int _trips;

    /// <summary>Creates the worker's watchdog.</summary>
    /// <param name="jobs">The application's store, where an attempt's cause is recorded.</param>
    /// <param name="exit">The process exit; the production implementation ends the process.</param>
    /// <param name="options">The grace and the tick.</param>
    /// <param name="clock">The clock the attempts' instants are read from.</param>
    /// <param name="logger">Where a trip is reported.</param>
    /// <param name="metrics">The worker's instruments, when the process carries them.</param>
    public RenderWatchdog(
        IJobStore jobs,
        IProcessExit exit,
        RenderWatchdogOptions options,
        TimeProvider clock,
        ILogger<RenderWatchdog> logger,
        PdfRenderingMetrics? metrics = null)
    {
        _jobs = jobs;
        _exit = exit;
        _options = options;
        _clock = clock;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>Whether this worker tripped on a render and is stopping.</summary>
    public bool IsUnhealthy => Volatile.Read(ref _trips) > 0;

    /// <summary>How many attempts the watchdog stopped, each one trip.</summary>
    public int Trips => Volatile.Read(ref _trips);

    /// <summary>Registers one attempt before its renderer is called.</summary>
    /// <param name="outputId">The output the attempt prints.</param>
    /// <param name="blockIndex">The block's position in the output's partition.</param>
    /// <param name="attempt">The attempt number the engine reported.</param>
    /// <param name="startedAt">When the attempt started, in UTC.</param>
    /// <param name="deadline">The attempt's start-to-close deadline.</param>
    /// <param name="heartbeats">The attempt's beats.</param>
    /// <returns>The registration the activity unregisters when it ends.</returns>
    public AttemptRegistration Register(
        string outputId,
        int blockIndex,
        int attempt,
        DateTimeOffset startedAt,
        TimeSpan deadline,
        CancellationTokenSource heartbeats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputId);
        ArgumentNullException.ThrowIfNull(heartbeats);

        var registration = new AttemptRegistration(outputId, blockIndex, attempt, startedAt, deadline, heartbeats);
        _attempts.TryAdd(registration, 0);
        return registration;
    }

    /// <summary>Removes an attempt that ended, whatever its outcome.</summary>
    /// <param name="registration">The registration the activity holds.</param>
    public void Unregister(AttemptRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _attempts.TryRemove(registration, out _);
    }

    /// <summary>
    /// Looks at every registered attempt at <paramref name="now"/> and stops the ones past
    /// their deadline and grace. Exposed to the tests, which move the clock themselves; the
    /// hosted loop calls it on the tick with the process clock.
    /// </summary>
    /// <param name="now">The instant the look is decided at, in UTC.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>Whether this look stopped at least one attempt.</returns>
    internal async Task<bool> EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stopped = false;
        foreach (var attempt in _attempts.Keys)
        {
            if (now < attempt.DeadlineAt + _options.Grace || !attempt.TryBeginTrip())
            {
                continue;
            }

            stopped = true;
            Interlocked.Increment(ref _trips);
            _metrics?.WatchdogExit(PdfRenderingMetrics.DeadlineExitReason);
            _logger.LogWarning(
                "Render watchdog: block {BlockIndex} of output {OutputId} past its deadline; stopping",
                attempt.BlockIndex,
                attempt.OutputId);

            attempt.StopHeartbeats();

            // The record is what the child reads, so it is written before the process ends.
            // A store that cannot answer does not hold the exit: the carried failure still
            // ends the output with RENDER_TIMEOUT, its reason falling back to "none".
            try
            {
                await _jobs.RecordAttemptCauseAsync(
                    new AttemptCause(
                        attempt.OutputId,
                        attempt.BlockIndex,
                        attempt.Attempt,
                        AttemptCauseVocabulary.WatchdogDeadline,
                        now),
                    cancellationToken).ConfigureAwait(false);
                _metrics?.AttemptCause(AttemptCauseVocabulary.WatchdogDeadline);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Render watchdog: the cause of block {BlockIndex} of output {OutputId} could not be recorded ({ExceptionType})",
                    attempt.BlockIndex,
                    attempt.OutputId,
                    exception.GetType().Name);
            }

            attempt.FailAttempt();
            _exit.Exit(ExitCode);
        }

        return stopped;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ticker = new PeriodicTimer(_options.Tick);
        while (await ticker.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await EvaluateAsync(_clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
        }
    }
}
