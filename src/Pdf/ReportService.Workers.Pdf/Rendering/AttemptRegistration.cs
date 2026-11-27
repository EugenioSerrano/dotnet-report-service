using ReportService.Templates;
using Temporalio.Exceptions;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// One in-flight block activity as the watchdog knows it: the attempt's own start, its
/// start-to-close deadline and its output, block and attempt, registered before the
/// renderer is called and unregistered when the activity ends. The watchdog acts on it
/// once: it stops the activity's beats, records the cause and builds the typed failure the
/// activity raises if the process lets it. No run id is kept: a continuation of the
/// output's execution changes the run, never the attempt a cause belongs to.
/// </summary>
internal sealed class AttemptRegistration
{
    private int _tripped;

    /// <summary>Creates the registration of one attempt.</summary>
    /// <param name="outputId">The output the attempt prints.</param>
    /// <param name="blockIndex">The block's position in the output's partition.</param>
    /// <param name="attempt">The attempt number the engine reported.</param>
    /// <param name="startedAt">When the attempt started, in UTC.</param>
    /// <param name="deadline">The attempt's start-to-close deadline.</param>
    /// <param name="heartbeats">The beats of the attempt, which the watchdog stops.</param>
    public AttemptRegistration(
        string outputId,
        int blockIndex,
        int attempt,
        DateTimeOffset startedAt,
        TimeSpan deadline,
        CancellationTokenSource heartbeats)
    {
        OutputId = outputId;
        BlockIndex = blockIndex;
        Attempt = attempt;
        StartedAt = startedAt;
        Deadline = deadline;
        Heartbeats = heartbeats;
    }

    /// <summary>The output the attempt prints.</summary>
    public string OutputId { get; }

    /// <summary>The block's position in the output's partition.</summary>
    public int BlockIndex { get; }

    /// <summary>The attempt number the engine reported.</summary>
    public int Attempt { get; }

    /// <summary>When the attempt started, in UTC.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>The attempt's start-to-close deadline.</summary>
    public TimeSpan Deadline { get; }

    /// <summary>The instant the attempt's own deadline passes.</summary>
    public DateTimeOffset DeadlineAt => StartedAt + Deadline;

    /// <summary>The beats of the attempt; the watchdog cancels it when it trips.</summary>
    public CancellationTokenSource Heartbeats { get; }

    /// <summary>Whether the watchdog has acted on this attempt.</summary>
    public bool Tripped => Volatile.Read(ref _tripped) != 0;

    /// <summary>Whether the attempt's beats were stopped by the watchdog.</summary>
    public bool HeartbeatsStopped { get; private set; }

    /// <summary>The typed failure the watchdog built for this attempt, or <see langword="null"/> before it trips.</summary>
    public ApplicationFailureException? Failure { get; private set; }

    /// <summary>
    /// Claims the one trip this registration allows. A finished or already-tripped attempt
    /// answers false, so two ticks of the watchdog can never act twice on one attempt.
    /// </summary>
    public bool TryBeginTrip() => Interlocked.Exchange(ref _tripped, 1) == 0;

    /// <summary>Stops the attempt's beats; the engine sees the heartbeat timeout and retries.</summary>
    public void StopHeartbeats()
    {
        HeartbeatsStopped = true;
        try
        {
            Heartbeats.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The activity ended between the trip and this call; nothing is left to stop.
        }
    }

    /// <summary>
    /// Builds the typed failure the activity raises when the SDK still lets it. The mapping
    /// does not depend on this succeeding: the recorded cause is what the child reads.
    /// </summary>
    public void FailAttempt() => Failure = new ApplicationFailureException(
        "RENDER_TIMEOUT: the render passed its deadline and the grace",
        errorType: TemplateErrorCodes.RenderTimeout,
        nonRetryable: false);
}
