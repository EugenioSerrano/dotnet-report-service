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

    /// <summary>
    /// Every log line the composition holds for the render worker, in the order the worker wrote
    /// them. It is the worker's own output — the lines its activities log — read through the
    /// composition's log stream, so an entry can prove that a block started and that no block above
    /// it was ever named.
    /// </summary>
    /// <param name="app">The running composition.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task<IReadOnlyList<string>> RenderLogAsync(
        DistributedApplication app,
        CancellationToken cancellationToken)
    {
        var logs = app.Services.GetRequiredService<ResourceLoggerService>();
        var lines = new List<string>();
        await foreach (var batch in logs
            .GetAllAsync(TestComposition.RenderWorkerName)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            lines.AddRange(batch.Select(line => line.Content));
        }

        return lines;
    }

    /// <summary>
    /// Waits until the render worker's log holds a line the entry requires before it can stop the
    /// worker inside the window — the line that says the attempt began, never a timing guess.
    /// </summary>
    /// <param name="app">The running composition.</param>
    /// <param name="what">What the line says, named in the diagnostic.</param>
    /// <param name="predicate">The condition one log line must satisfy.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>Every line the log held when the wait ended.</returns>
    public static async Task<IReadOnlyList<string>> WaitForRenderLogAsync(
        DistributedApplication app,
        string what,
        Func<string, bool> predicate,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> held = [];
        await Wait.UntilAsync(
            $"the render worker's log to hold {what}",
            async token =>
            {
                held = await RenderLogAsync(app, token).ConfigureAwait(false);
                return held.Any(predicate);
            },
            LogDeadline,
            PollInterval,
            cancellationToken).ConfigureAwait(false);
        return held;
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
