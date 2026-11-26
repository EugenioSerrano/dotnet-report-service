using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The restart the five entries of <see cref="CancelRestartEndToEndTests"/> share: it stops and
/// starts the composition's own resources through their own commands, and reads the composition's
/// own database and log streams. It adds no product hook and no test-only switch: a stopped worker
/// is the product's own process stopped, and every window is a fact read from durable state, never
/// a sleep (US-010.BOLT-003 SPEC §6, §8.2).
/// <para>
/// The stop's contract is <see cref="KnownResourceCommands.StopCommand"/> followed by the resource
/// reaching a terminal state — the process is gone, not draining — and the caller asserts the
/// durable state the window requires <i>while it is stopped</i>. The start's contract is
/// <see cref="KnownResourceCommands.StartCommand"/> followed by the resource running again. A
/// command the composition refuses, or a state it does not reach, fails with a named diagnostic:
/// the harness never weakens an assertion to make a window appear.
/// </para>
/// </summary>
internal static class RestartHarness
{
    /// <summary>The orchestration worker's resource name: the host process that dispatches and runs the workflows.</summary>
    public const string OrchestrationWorkerName = "orchestration-worker";

    /// <summary>The kind of the durable request a consumer's cancellation is recorded as.</summary>
    public const string CancelJobKind = "cancel-job";

    /// <summary>The interval a poll repeats its read at.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a resource may take to stop or to start.</summary>
    public static readonly TimeSpan LifecycleDeadline = TimeSpan.FromMinutes(2);

    /// <summary>How long the composition's log stream may take to hold a line the entry waits for.</summary>
    public static readonly TimeSpan LogDeadline = TimeSpan.FromMinutes(2);

    /// <summary>How long the durable cancel request may take to be readable after its answer.</summary>
    public static readonly TimeSpan CancelInstantDeadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Stops one of the composition's resources through its own command and waits until it reached a
    /// terminal state: after this returns the process is gone, so the durable state read then is the
    /// window the entry needs and no work of that process can change it.
    /// </summary>
    /// <param name="app">The running composition.</param>
    /// <param name="resource">The resource's name in the composition's model.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task StopAsync(DistributedApplication app, string resource, CancellationToken cancellationToken)
    {
        await ExecuteAsync(app, resource, KnownResourceCommands.StopCommand, "stop", cancellationToken).ConfigureAwait(false);
        await WaitForStateAsync(
            app,
            resource,
            [KnownResourceStates.Exited, KnownResourceStates.Finished],
            $"{resource} to stop",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts one of the composition's resources through its own command and waits until it runs
    /// again. A container the platform replaces and a host process the composition restarts take the
    /// same command; nothing here knows which a resource is.
    /// </summary>
    /// <param name="app">The running composition.</param>
    /// <param name="resource">The resource's name in the composition's model.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task StartAsync(DistributedApplication app, string resource, CancellationToken cancellationToken)
    {
        await ExecuteAsync(app, resource, KnownResourceCommands.StartCommand, "start", cancellationToken).ConfigureAwait(false);
        await WaitForStateAsync(app, resource, [KnownResourceStates.Running], $"{resource} to run", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the composition reports the resource healthy — for the render worker, that the
    /// browser inside the restarted container answers its own health check.
    /// </summary>
    /// <param name="app">The running composition.</param>
    /// <param name="resource">The resource's name in the composition's model.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task WaitUntilHealthyAsync(DistributedApplication app, string resource, CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await Wait.UntilAsync(
            $"{resource} to report healthy after its restart",
            token =>
            {
                var healthy = notifications.TryGetCurrentState(resource, out var current)
                    && current.Snapshot.HealthStatus == HealthStatus.Healthy;
                return Task.FromResult(healthy);
            },
            LifecycleDeadline,
            PollInterval,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Repeats a read of the composition's own records until it answers true, with the wait's own
    /// named diagnostic when the deadline passes. Every assertion of an entry is one of these: a
    /// durable fact that settles, never a fixed sleep.
    /// </summary>
    /// <param name="what">What the wait is for, named in the diagnostic.</param>
    /// <param name="predicate">The read and its condition.</param>
    /// <param name="deadline">How long the fact may take to hold.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static Task WaitForAsync(
        string what,
        Func<CancellationToken, Task<bool>> predicate,
        TimeSpan deadline,
        CancellationToken cancellationToken) =>
        Wait.UntilAsync(what, predicate, deadline, PollInterval, cancellationToken);

    /// <summary>
    /// The instant the cancellation of a job was requested, as the store already holds it: the
    /// <c>created_at</c> of its <c>cancel-job</c> request. It is the instant every "after the
    /// request" predicate of the entry is measured from.
    /// </summary>
    /// <param name="dataSource">The composition's metadata database.</param>
    /// <param name="reportJobId">The job whose request is read.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>The instant the request was recorded, in UTC.</returns>
    public static async Task<DateTimeOffset> CancelInstantAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? instant = null;
        await Wait.UntilAsync(
            $"the cancel-job request of {reportJobId} to be readable",
            async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT min(created_at) FROM report_service.intents WHERE kind = @kind AND subject_id = @id";
                command.Parameters.Add(new NpgsqlParameter("kind", CancelJobKind));
                command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
                var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (value is DateTime recorded)
                {
                    instant = new DateTimeOffset(DateTime.SpecifyKind(recorded, DateTimeKind.Utc), TimeSpan.Zero);
                    return true;
                }

                return false;
            },
            CancelInstantDeadline,
            PollInterval,
            cancellationToken).ConfigureAwait(false);

        return instant
            ?? throw new InvalidOperationException($"the cancel-job request of {reportJobId} was readable but carried no instant");
    }

    private static async Task ExecuteAsync(
        DistributedApplication app,
        string resource,
        string commandName,
        string commandText,
        CancellationToken cancellationToken)
    {
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        var result = await commands.ExecuteCommandAsync(resource, commandName, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"the composition refused to {commandText} {resource}: {result.Message}");
        }
    }

    private static async Task WaitForStateAsync(
        DistributedApplication app,
        string resource,
        IReadOnlyList<string> states,
        string what,
        CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        string? observed = null;
        try
        {
            await Wait.UntilAsync(
                what,
                token =>
                {
                    observed = CurrentState(notifications, resource);
                    return Task.FromResult(observed is not null && states.Contains(observed, StringComparer.Ordinal));
                },
                LifecycleDeadline,
                PollInterval,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"{what} did not happen within {(int)LifecycleDeadline.TotalSeconds} s; last observed state '{observed ?? "none"}', expected one of [{string.Join(", ", states)}].",
                exception);
        }
    }

    private static string? CurrentState(ResourceNotificationService notifications, string resource) =>
        notifications.TryGetCurrentState(resource, out var current) ? current.Snapshot.State?.Text : null;
}

/// <summary>
/// The render worker's own log, tapped once and buffered: one live subscription to the composition's
/// log stream for the resource, opened when the tap is created and read from the buffer afterwards.
/// <para>
/// The instrument is the live stream, not a snapshot. <see cref="ResourceLoggerService.WatchAsync"/>
/// delivers the lines already written and then the lines as they are written, and stays open while
/// the resource lives; <see cref="ResourceLoggerService.GetAllAsync"/> is a DI-backed console-log
/// read that completes, so re-reading it on every poll misses a one-shot line written between two
/// reads — a render attempt's <c>Block … started, attempt …</c> line is written once, when the
/// attempt begins, and must be observed rather than re-read (the cause of the integration run
/// <c>261126-2026</c>'s R2/R3/R5 failures). The tap is therefore created <b>before</b> the job is
/// submitted, so the line cannot precede the subscription.
/// </para>
/// <para>
/// Waits read the buffer with the harness's bounded poll and its named diagnostic: a line that never
/// arrives still fails the entry after <see cref="RestartHarness.LogDeadline"/>, naming what was
/// awaited. Nothing here weakens an assertion — it makes the same assertions observable.
/// </para>
/// </summary>
internal sealed class RenderLogTap : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();
    private readonly Task _pump;
    private bool _closed;

    /// <summary>Opens the one subscription and starts buffering the render worker's lines.</summary>
    /// <param name="app">The running composition.</param>
    public RenderLogTap(DistributedApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logs = app.Services.GetRequiredService<ResourceLoggerService>();
        _pump = PumpAsync(logs, _stop.Token);
    }

    /// <summary>Every line the render worker wrote since the tap was opened, in the order it wrote them.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>
    /// Whether the composition completed the resource's log stream. A completed stream never
    /// delivers another line — the composed line that a later assertion waits for is then
    /// unobservable, and the wait says so instead of passing on an empty read.
    /// </summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>The lines written after the first <paramref name="count"/> lines of the buffer.</summary>
    /// <param name="count">How many lines the buffer held when the boundary was taken.</param>
    public IReadOnlyList<string> Since(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_gate)
        {
            return [.. _lines.Skip(count)];
        }
    }

    /// <summary>
    /// Waits until the buffer holds a line the entry requires before it can close a window — the line
    /// that says the render attempt began, never a timing guess — and answers the whole buffer when
    /// the wait ends.
    /// </summary>
    /// <param name="what">What the line says, named in the diagnostic.</param>
    /// <param name="predicate">The condition one log line must satisfy.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>Every line the buffer held when the wait ended.</returns>
    public async Task<IReadOnlyList<string>> WaitForAsync(
        string what,
        Func<string, bool> predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        IReadOnlyList<string> held = [];
        await Wait.UntilAsync(
            $"the render worker's log to hold {what}",
            token =>
            {
                held = Lines;
                return Task.FromResult(held.Any(predicate));
            },
            RestartHarness.LogDeadline,
            RestartHarness.PollInterval,
            cancellationToken).ConfigureAwait(false);
        return held;
    }

    /// <summary>
    /// Waits until a line was written after the buffer held <paramref name="count"/> lines: the proof
    /// that the stream is still live for the process restarted after that boundary. It is what makes
    /// the entries' "no line after the restart names …" assertions observe something instead of
    /// passing on an empty read; a stream the composition completed fails the wait, naming it.
    /// </summary>
    /// <param name="count">How many lines the buffer held when the boundary was taken.</param>
    /// <param name="what">What the line proves, named in the diagnostic.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task WaitForLineAfterAsync(int count, string what, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        try
        {
            await Wait.UntilAsync(
                $"the render worker's log to hold {what}",
                token =>
                {
                    lock (_gate)
                    {
                        return Task.FromResult(_lines.Count > count);
                    }
                },
                RestartHarness.LogDeadline,
                RestartHarness.PollInterval,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"the render worker's log did not hold {what} within {(int)RestartHarness.LogDeadline.TotalSeconds} s; "
                + $"the buffer holds {Lines.Count} line(s) and the composition's stream for {TestComposition.RenderWorkerName} is "
                + (IsClosed ? "closed (a completed stream delivers no further line)" : "open but silent")
                + ".",
                exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The subscription was cancelled: the tap is being disposed.
        }

        _stop.Dispose();
    }

    private async Task PumpAsync(ResourceLoggerService logs, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in logs
                .WatchAsync(TestComposition.RenderWorkerName)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                lock (_gate)
                {
                    _lines.AddRange(batch.Select(line => line.Content));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // The stream ended in a way the next subscription could not resume: a completed stream
            // delivers nothing more, so the tap records it and every later wait names it.
        }

        lock (_gate)
        {
            _closed = true;
        }
    }
}
