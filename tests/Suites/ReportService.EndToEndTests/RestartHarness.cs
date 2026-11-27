using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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

    /// <summary>The container runtime's own CLI, the one the composition's processes run under.</summary>
    private const string DockerCli = "docker";

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

    /// <summary>
    /// Kills the container with <paramref name="containerName"/> the way the watchdog's remedy
    /// kills it: an abrupt process death (<c>docker kill</c>, SIGKILL), not a graceful teardown. The
    /// in-flight work of that process is lost — a running render attempt cannot acknowledge a
    /// cancellation — and the platform's restart policy replaces the container, exactly as it
    /// replaces a worker the watchdog stopped (ADR-012 §3.9(3), SPEC §6 Phase D).
    /// </summary>
    /// <param name="containerName">The exact name of the container to kill.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task KillContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        await RunDockerAsync(["kill", "--signal", "KILL", containerName], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the runtime restarted the container with <paramref name="containerName"/> after
    /// <paramref name="killedAt"/> — the platform's replacement of a killed worker — and answers the
    /// restarted process's own start instant, which is the boundary a re-established log stream is
    /// filtered at. The container's start instant, not its restart count, is the evidence: a
    /// restart the daemon's policy performs and one the platform performs both move it forward.
    /// </summary>
    /// <param name="containerName">The exact name of the container to watch.</param>
    /// <param name="killedAt">The instant before the kill; a start after it is the new process.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>The restarted container's <c>State.StartedAt</c>, in UTC.</returns>
    public static async Task<DateTimeOffset> WaitForContainerRestartAsync(
        string containerName,
        DateTimeOffset killedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        var startedAt = DateTimeOffset.MinValue;
        await Wait.UntilAsync(
            $"the platform to start the render worker container '{containerName}' again after the kill",
            async token =>
            {
                var text = await InspectAsync(containerName, "{{.State.StartedAt}}", token).ConfigureAwait(false);
                return DateTimeOffset.TryParse(
                        text.Trim(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out startedAt)
                    && startedAt > killedAt;
            },
            LifecycleDeadline,
            PollInterval,
            cancellationToken).ConfigureAwait(false);
        return startedAt;
    }

    /// <summary>The <c>docker inspect</c> field <paramref name="format"/> of one container, as the runtime prints it.</summary>
    /// <param name="containerName">The exact name of the container to inspect.</param>
    /// <param name="format">The Go-template field to print, e.g. <c>{{.State.Running}}</c>.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    internal static Task<string> InspectAsync(string containerName, string format, CancellationToken cancellationToken) =>
        RunDockerAsync(["inspect", "--format", format, containerName], cancellationToken);

    private static async Task<string> RunDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(DockerCli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"the container runtime's CLI could not be launched: docker {string.Join(' ', arguments)}");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        return output;
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
/// The render worker's own log, read from the container runtime and buffered: one
/// <c>docker logs --follow</c> of the container this composition declared, opened before the first
/// job is submitted, and re-established at the restarted container's start instant after a stop.
/// <para>
/// The instrument is the container runtime's own stream. A container's stdout never reaches
/// <see cref="ResourceLoggerService"/>'s in-memory resource log — that state carries a project's own
/// process output — so its <c>WatchAsync</c> and <c>GetAllAsync</c> both read something else; runs
/// <c>261126-2026</c> and <c>261126-2146</c> falsified the first and refined the second. The
/// runtime's stream is the one instrument that sees the container's lines (SPEC §5, §6 B.1, D12).
/// </para>
/// <para>
/// The container is addressed by the name the composition declared in its own model
/// (<see cref="TestComposition.DeclaredRenderWorkerContainerName"/>), never by a listing of the
/// daemon: a name the runtime does not hold fails loudly with that name, and no other container is
/// ever considered. A stream ends when the platform stops or replaces the container, and the tap
/// does not pretend it continues — <see cref="ResumeAsync"/> opens a new stream with
/// <c>--since &lt;boundary&gt;</c>, so only the restarted process's lines enter after the boundary,
/// and a stream that ended or faulted says so in every later wait's diagnostic.
/// </para>
/// <para>
/// Waits read the buffer with the harness's bounded poll and its named diagnostic: a line that never
/// arrives still fails the entry after <see cref="RestartHarness.LogDeadline"/>, naming what was
/// awaited, with the stream's own state beside it. Nothing here weakens an assertion — it makes the
/// same assertions observable.
/// </para>
/// </summary>
internal sealed class RenderLogTap : IAsyncDisposable
{
    private const string Docker = "docker";

    private readonly string _containerName;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _stream;
    private Task _pump = Task.CompletedTask;
    private StreamState _state = StreamState.Running;
    private string? _fault;

    private RenderLogTap(string containerName) => _containerName = containerName;

    /// <summary>
    /// Opens the tap on the container the composition declared: it waits, bounded, until the runtime
    /// holds that name and the container runs, then starts one <c>docker logs --follow</c> and
    /// buffers everything it prints. Waiting for the container to run — not merely to exist — keeps
    /// the stream from ending on a container that was created but had not started when the tap
    /// opened.
    /// </summary>
    /// <param name="containerName">The composition's declared render-worker container name.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>The tap the entry reads the render worker's lines through.</returns>
    public static async Task<RenderLogTap> OpenAsync(string containerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        await Wait.UntilAsync(
            $"the composition's render worker container '{containerName}' to run",
            async token =>
            {
                var running = await RestartHarness.InspectAsync(containerName, "{{.State.Running}}", token).ConfigureAwait(false);
                return running.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            },
            RestartHarness.LifecycleDeadline,
            RestartHarness.PollInterval,
            cancellationToken).ConfigureAwait(false);

        var tap = new RenderLogTap(containerName);
        tap.StartStream(since: null);
        return tap;
    }

    /// <summary>Every line the container wrote since the tap was opened, in the order it wrote them.</summary>
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
    /// Re-establishes the stream at the restarted container, after the entry observed the resource
    /// running. The boundary is the container's own <c>State.StartedAt</c>, read from the runtime;
    /// <paramref name="observedRunningAt"/> — the instant the entry saw it running — is the boundary
    /// when the runtime reports none. The stream that served the stopped container is ended first,
    /// so only the restarted process's lines enter the buffer from here on.
    /// </summary>
    /// <param name="observedRunningAt">The instant the entry observed the resource running.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>
    /// How many lines the buffer held when the re-established stream began: the index boundary the
    /// entry's post-restart assertions read from, so a line the ended stream flushed as it stopped
    /// can never be mistaken for one the restarted process wrote.
    /// </returns>
    public async Task<int> ResumeAsync(DateTimeOffset observedRunningAt, CancellationToken cancellationToken)
    {
        await EndStreamAsync().ConfigureAwait(false);
        var startedAt = await StartedAtAsync(cancellationToken).ConfigureAwait(false);
        int boundary;
        lock (_gate)
        {
            boundary = _lines.Count;
        }

        StartStream(startedAt ?? observedRunningAt);
        return boundary;
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
        try
        {
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
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"{exception.Message} {BufferDetail()}", exception);
        }

        return held;
    }

    /// <summary>
    /// Waits until a line was written after the buffer held <paramref name="count"/> lines: the proof
    /// that the re-established stream is live for the process restarted after that boundary. It is
    /// what makes the entries' "no line after the restart names …" assertions observe something
    /// instead of passing on an empty read; a stream that ended or faulted fails the wait, naming it.
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
            throw new TimeoutException($"{exception.Message} {BufferDetail()}", exception);
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
            // The stream was cancelled: the tap is being disposed.
        }

        _stream?.Dispose();
        _stop.Dispose();
    }

    private void StartStream(DateTimeOffset? since)
    {
        _stream?.Dispose();
        _stream = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        lock (_gate)
        {
            _state = StreamState.Running;
            _fault = null;
        }

        _pump = FollowAsync(since, _stream.Token);
    }

    private async Task EndStreamAsync()
    {
        if (_stream is { } stream)
        {
            await stream.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stream was cancelled by this call: that is what "end it" means here.
        }
    }

    private async Task FollowAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "logs", "--follow" };
        if (since is { } boundary)
        {
            arguments.Add("--since");
            arguments.Add(boundary.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
        }

        arguments.Add(_containerName);

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo(Docker)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordFault($"the stream could not be started: {exception.Message}");
            return;
        }

        if (process is null)
        {
            RecordFault("the stream could not be started: the container runtime's CLI could not be launched");
            return;
        }

        using (process)
        {
            var errors = new StringBuilder();
            var output = ReadLinesAsync(process.StandardOutput, cancellationToken);
            var error = ReadErrorLinesAsync(process.StandardError, errors, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Kill(process);
            }

            // Both readers treat the tap's own cancellation as the end of their work, so this never
            // throws for it: a deliberate end is not the runtime's answer.
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            lock (_gate)
            {
                _state = process.ExitCode == 0 ? StreamState.Ended : StreamState.Faulted;
                _fault = process.ExitCode == 0 ? null : errors.ToString().Trim();
            }
        }
    }

    private async Task ReadLinesAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lock (_gate)
                {
                    _lines.Add(line);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordFault($"the stream could not be read: {exception.Message}");
        }
    }

    private static async Task ReadErrorLinesAsync(
        StreamReader reader,
        StringBuilder destination,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                destination.AppendLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The runtime's error stream failing on its own is reported by the exit-code path.
        }
    }

    private async Task<DateTimeOffset?> StartedAtAsync(CancellationToken cancellationToken)
    {
        var text = await RestartHarness.InspectAsync(_containerName, "{{.State.StartedAt}}", cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.TryParse(
            text.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var startedAt)
            ? startedAt.ToUniversalTime()
            : null;
    }

    private void RecordFault(string fault)
    {
        lock (_gate)
        {
            _state = StreamState.Faulted;
            _fault = fault;
        }
    }

    private string BufferDetail()
    {
        int count;
        lock (_gate)
        {
            count = _lines.Count;
        }

        return $"{DescribeStream()}; the buffer holds {count} line(s).";
    }

    private string DescribeStream()
    {
        lock (_gate)
        {
            return _state switch
            {
                StreamState.Faulted => $"the container stream of '{_containerName}' faulted: {_fault ?? "the runtime reported no reason"}",
                StreamState.Ended => $"the container stream of '{_containerName}' ended (the container stopped) and was not re-established",
                _ => $"the container stream of '{_containerName}' is running",
            };
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private enum StreamState
    {
        Running,
        Ended,
        Faulted,
    }
}
