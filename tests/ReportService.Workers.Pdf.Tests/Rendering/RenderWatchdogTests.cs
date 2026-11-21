using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.Testing;
using ReportService.Workers.Pdf.Rendering;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The render watchdog: no trip before the attempt's own deadline and the grace, a trip at
/// the grace and one second after, one trip per attempt however often it is looked at, a
/// finished attempt never tripped, and the five actions of a trip - the log and the unhealthy
/// mark, the stopped beats, the recorded cause, the typed failure and the exit - in that order,
/// with the exit last. The clock is the test's own and the exit is replaced, so nothing waits
/// for real time and no process ends.
/// </summary>
public sealed class RenderWatchdogTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(300);

    private const string OutputId = "output-0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task NoTripBeforeTheDeadlineAndItsGrace()
    {
        using var rig = new Rig();

        Assert.False(await rig.EvaluateAsync(Now + Deadline + TimeSpan.FromSeconds(29) + TimeSpan.FromMilliseconds(999)));
        Assert.Empty(rig.Exit.Calls);
        Assert.Equal(0, rig.Watchdog.Trips);
        Assert.False(rig.Watchdog.IsUnhealthy);
        Assert.Empty(rig.Jobs.Causes);
        Assert.False(rig.Registration!.HeartbeatsStopped);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public async Task ATripHappensAtTheGraceAndASecondAfter(int secondsAfterTheGrace)
    {
        using var rig = new Rig();

        Assert.True(await rig.EvaluateAsync(Now + Deadline + TimeSpan.FromSeconds(secondsAfterTheGrace)));

        Assert.Single(rig.Exit.Calls);
        Assert.Equal(1, rig.Watchdog.Trips);
        Assert.True(rig.Watchdog.IsUnhealthy);
        Assert.Single(rig.Jobs.Causes);
        Assert.Equal(AttemptCauseVocabulary.WatchdogDeadline, rig.Jobs.Causes[0].Cause);
        Assert.Equal(1, rig.Jobs.Causes[0].Attempt);
        Assert.Equal(0, rig.Jobs.Causes[0].BlockIndex);
        Assert.Equal(OutputId, rig.Jobs.Causes[0].OutputId);
    }

    [Fact]
    public async Task OneTripPerAttemptHoweverOftenItIsLookedAt()
    {
        using var rig = new Rig();
        var after = Now + Deadline + TimeSpan.FromSeconds(30);

        Assert.True(await rig.EvaluateAsync(after));
        Assert.False(await rig.EvaluateAsync(after.AddSeconds(1)));
        Assert.False(await rig.EvaluateAsync(after.AddSeconds(10)));

        Assert.Single(rig.Exit.Calls);
        Assert.Single(rig.Jobs.Causes);
        Assert.Equal(1, rig.Watchdog.Trips);
    }

    [Fact]
    public async Task TheFiveActionsHappenInOrderBeforeTheExit()
    {
        using var rig = new Rig();

        await rig.EvaluateAsync(Now + Deadline + TimeSpan.FromSeconds(30));

        // The exit is the last action: at the instant it is called, every other action has
        // already happened, and the beats were stopped before the cause was written.
        var exit = Assert.Single(rig.Exit.Calls);
        Assert.True(exit.LogLines > 0);
        Assert.True(exit.IsUnhealthy);
        Assert.True(exit.HeartbeatsStopped);
        Assert.True(exit.CauseRecorded);
        Assert.True(exit.FailureBuilt);
        Assert.True(rig.HeartbeatsStoppedWhenCauseRecorded);
        Assert.Contains(rig.Logs.Lines, line => line.Contains("Render watchdog", StringComparison.Ordinal));
        Assert.Contains(rig.Logs.Lines, line => line.Contains("past its deadline; stopping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFinishedAttemptIsNeverTripped()
    {
        using var rig = new Rig(register: false);
        using var heartbeats = new CancellationTokenSource();
        var registration = rig.Watchdog.Register(OutputId, 0, 1, Now, Deadline, heartbeats);
        rig.Watchdog.Unregister(registration);

        Assert.False(await rig.EvaluateAsync(Now + Deadline + TimeSpan.FromMinutes(5)));
        Assert.Empty(rig.Exit.Calls);
        Assert.Empty(rig.Jobs.Causes);
        Assert.Equal(0, rig.Watchdog.Trips);
    }

    [Fact]
    public async Task TwoAttemptsOfOneBlockGiveAnExitAndARowForEach()
    {
        using var rig = new Rig(register: false);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var attemptOne = rig.Watchdog.Register(OutputId, 0, 1, Now, Deadline, first);
        var attemptTwo = rig.Watchdog.Register(OutputId, 0, 2, Now, Deadline, second);

        Assert.True(await rig.EvaluateAsync(Now + Deadline + TimeSpan.FromSeconds(30)));

        Assert.Equal(2, rig.Exit.Calls.Count);
        Assert.Equal(2, rig.Watchdog.Trips);
        Assert.Equal([1, 2], rig.Jobs.Causes.Select(cause => cause.Attempt).Order());
        Assert.True(attemptOne.Tripped);
        Assert.True(attemptTwo.Tripped);
        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public async Task TheHealthCheckReportsTheStateBeforeAndAfterTheTrip()
    {
        using var rig = new Rig();
        var health = new RenderWatchdogHealthCheck(rig.Watchdog);

        var before = await health.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, before.Status);
        Assert.Null(before.Description);

        await rig.EvaluateAsync(Now + Deadline + TimeSpan.FromSeconds(30));

        var after = await health.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, after.Status);
        Assert.Equal(RenderWatchdogHealthCheck.UnhealthyDescription, after.Description);
        Assert.DoesNotContain(OutputId, after.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionsAreTheAdrsValuesAndAValueSetIsValidatedByName()
    {
        var options = RenderWatchdogOptions.Create();

        Assert.Equal(30, options.GraceSeconds);
        Assert.Equal(5, options.TickSeconds);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Grace);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Tick);
        Assert.Empty(options.Validate());

        var problems = new RenderWatchdogOptions(0, 0).Validate();
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Setting == RenderWatchdogOptions.GraceSetting);
        Assert.Contains(problems, problem => problem.Setting == RenderWatchdogOptions.TickSetting);
        Assert.DoesNotContain("0", string.Join(" ", problems.Select(problem => problem.ToString())), StringComparison.Ordinal);
    }

    /// <summary>One watchdog with one registered attempt, its store, its logs and its replaced exit.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly CancellationTokenSource? _heartbeats;

        public Rig(bool register = true)
        {
            Watchdog = new RenderWatchdog(Jobs, Exit, RenderWatchdogOptions.Create(), Clock, Logs.Factory.CreateLogger<RenderWatchdog>());
            Exit.Watchdog = Watchdog;
            Exit.LogLineCount = () => Logs.Lines.Count;
            Exit.CauseCount = () => Jobs.Causes.Count;
            if (register)
            {
                _heartbeats = new CancellationTokenSource();
                Registration = Watchdog.Register(OutputId, 0, 1, Now, Deadline, _heartbeats);
                Exit.Registration = Registration;
            }

            Jobs.OnRecordCause = () => HeartbeatsStoppedWhenCauseRecorded = Registration is not null && Registration.HeartbeatsStopped;
        }

        public ManualTimeProvider Clock { get; } = new(Now);

        public FakeJobStore Jobs { get; } = new();

        public RecordingExit Exit { get; } = new();

        public CapturingLogs Logs { get; } = new();

        public RenderWatchdog Watchdog { get; }

        public AttemptRegistration? Registration { get; }

        public bool HeartbeatsStoppedWhenCauseRecorded { get; private set; }

        public Task<bool> EvaluateAsync(DateTimeOffset now) =>
            Watchdog.EvaluateAsync(now, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _heartbeats?.Dispose();
            Logs.Dispose();
        }
    }

    /// <summary>The exit a case replaces the process end with: it records what had happened by then.</summary>
    private sealed class RecordingExit : IProcessExit
    {
        public List<(bool IsUnhealthy, bool HeartbeatsStopped, bool CauseRecorded, bool FailureBuilt, int LogLines)> Calls { get; } = [];

        public RenderWatchdog? Watchdog { get; set; }

        public AttemptRegistration? Registration { get; set; }

        public Func<int>? LogLineCount { get; set; }

        public Func<int>? CauseCount { get; set; }

        public void Exit(int exitCode)
        {
            Assert.Equal(RenderWatchdog.ExitCode, exitCode);
            Calls.Add((
                Watchdog?.IsUnhealthy ?? false,
                Registration?.HeartbeatsStopped ?? false,
                (CauseCount?.Invoke() ?? 0) > 0,
                Registration?.Failure is not null,
                LogLineCount?.Invoke() ?? 0));
        }
    }
}
