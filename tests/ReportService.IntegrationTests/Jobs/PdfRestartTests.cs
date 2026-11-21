extern alias pdf;
extern alias orchestration;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Templates;
using ReportService.Testing;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Client;
using Temporalio.Common;
using PdfRendering = pdf::ReportService.Workers.Pdf.Rendering;
using PdfOutputStepOptions = orchestration::ReportService.Workers.Orchestration.Jobs.RenderPdfBlockStepOptions;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The restart and the exhausted budget on the worker-level composition: the product's own API,
/// orchestration worker and render worker hosted in-process, against a real PostgreSQL 18.6
/// database, a real Temporal engine, one storage root and the class's template source. Nothing
/// here kills a process - the hosts are disposed and started again, which is the stop this
/// level can make honestly - so the unclean kills live in the composition-level class.
/// <para>
/// The deadline cases are seated through the test-only stall the product carries for them: the
/// composition phase is stalled after the guard's clock has started, and the print phase inside
/// the print step and before the printer is asked for a document. The stall is non-cancellable
/// by design, which is what makes the guard and the watchdog the only things that can end it.
/// </para>
/// </summary>
public sealed class PdfRestartTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Terminal = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan TripAndRecovery = TimeSpan.FromMinutes(14);

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PdfRestartTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The guard's deadline is reached on every attempt: the composition passes its 60 s bound
    /// three times, the third failure ends the output and the job with the timeout code and the
    /// cause the guard recorded, and nothing is published. The cause is not carried by the
    /// failure alone: the child reads the row the guard wrote.
    /// </summary>
    [Fact]
    public async Task TheGuardDeadlineOnEveryAttemptEndsTheOutputWithTheTimeout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        var stall = new SwitchableStall(RenderPhase.Composition, TimeSpan.FromSeconds(61));
        rig.InstallStall(stall);
        await rig.StartPdfAsync(cancellationToken);
        await rig.SeedTakenVersionAsync(PrintingRig.First, cancellationToken);

        var jobId = await SubmitAsync(rig, PrintingRig.First, cancellationToken);
        var row = await rig.WaitForTerminalAsync(jobId, Terminal, cancellationToken);

        Assert.Equal("FAILED", row.Status);
        Assert.Equal("RENDER_TIMEOUT", row.ErrorCode);
        Assert.Equal("FAILED", row.OutputStatus);
        Assert.Equal("RENDER_TIMEOUT", row.OutputErrorCode);
        Assert.Equal("exhausted cause=guard-deadline", row.OutputErrorReason);
        Assert.Null(row.ArtifactId);
        Assert.Null(row.ObjectKey);
        Assert.Equal(0, await rig.CountArtifactsAsync(jobId, cancellationToken));
        Assert.Equal(3, stall.Calls);

        var outputId = Assert.IsType<string>(row.OutputId);
        var child = await rig.OutputHistoryAsync(jobId, cancellationToken);
        Assert.Equal(3, Started(child, PdfRestartFacts.RenderPdfBlock));
        Assert.Equal(1, Scheduled(child, PdfRestartFacts.FindLastAttemptCause));
        Assert.Equal(0, Scheduled(child, PdfRestartFacts.PublishOutput));
        var cause = await rig.Jobs.FindLastAttemptCauseAsync(outputId, 0, cancellationToken);
        Assert.NotNull(cause);
        Assert.Equal(AttemptCauseVocabulary.GuardDeadline, cause!.Cause);
        await PdfRestartFacts.AssertInsideTheBound(child, cancellationToken);
    }

    /// <summary>
    /// The deterministic refusal is one attempt and never the exhaustion arm: the package whose
    /// anchors are undeclared fails the block with its own code, no cause is recorded, the child
    /// reads nothing and the job carries the refusal's code.
    /// </summary>
    [Fact]
    public async Task ADeterministicRefusalConsumesOneAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        await rig.SeedTakenVersionAsync(PrintingRig.NoAnchors, cancellationToken);

        var jobId = await SubmitAsync(rig, PrintingRig.NoAnchors, cancellationToken);
        var row = await rig.WaitForTerminalAsync(jobId, Terminal, cancellationToken);

        Assert.Equal("FAILED", row.Status);
        Assert.Equal("PAGINATION_FAILED", row.ErrorCode);
        Assert.Equal("PAGINATION_FAILED", row.OutputErrorCode);
        Assert.Null(row.ArtifactId);

        var outputId = Assert.IsType<string>(row.OutputId);
        var child = await rig.OutputHistoryAsync(jobId, cancellationToken);
        Assert.Equal(1, Started(child, PdfRestartFacts.RenderPdfBlock));
        Assert.Equal(0, Scheduled(child, PdfRestartFacts.FindLastAttemptCause));
        Assert.Equal(0, Scheduled(child, PdfRestartFacts.PublishOutput));
        Assert.Null(await rig.Jobs.FindLastAttemptCauseAsync(outputId, 0, cancellationToken));
        await PdfRestartFacts.AssertInsideTheBound(child, cancellationToken);
    }

    /// <summary>
    /// One watchdog trip, observed live: the first attempt passes its start-to-close deadline and
    /// the grace, the watchdog records the cause, marks the worker unhealthy and calls the exit
    /// seam; the host is stopped and started again; the second attempt prints the block and the
    /// job ends succeeded with one artifact. The OS-level exit is not observed - the seam records
    /// the call instead, and the recovery is the fresh host's.
    /// </summary>
    [Fact]
    public async Task OneWatchdogTripIsRecoveredOnAFreshHost()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var exit = new RecordingExit();
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            configurePdfServices: services =>
            {
                services.RemoveAll<PdfRendering.IProcessExit>();
                services.AddSingleton<PdfRendering.IProcessExit>(exit);
            },
            cancellationToken: cancellationToken);
        var stall = new SwitchableStall(RenderPhase.Print, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
        rig.InstallStall(stall);
        await rig.StartPdfAsync(cancellationToken);
        await rig.SeedTakenVersionAsync(PrintingRig.First, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, await rig.PdfHealthAsync(cancellationToken));

        using var metrics = new MetricCapture();
        var submitted = Stopwatch.GetTimestamp();
        var jobId = await SubmitAsync(rig, PrintingRig.First, cancellationToken);
        await ReportService.Testing.Wait.UntilAsync(
            "the watchdog to stop the first attempt",
            _ => Task.FromResult(exit.Calls > 0),
            TimeSpan.FromMinutes(7),
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        // The trip's latency against the bound it is supposed to keep: the deadline plus the
        // grace, measured from the child's start and recorded as an observation.
        var latency = Stopwatch.GetElapsedTime(submitted);
        var bound = PdfOutputStepOptions.StartToClose + PdfRendering.RenderWatchdogOptions.Create().Grace;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"the watchdog tripped {latency.TotalSeconds:F1}s after the child started; its bound is {bound.TotalSeconds:F0}s, so it waited {latency.TotalSeconds - bound.TotalSeconds:F1}s beyond it."));

        // The trip is recorded before the process would end: the worker is unhealthy, and the
        // cause is in the store whatever happens to this host next. The observation is recorded
        // either way - the exit seam is what this level can catch, never the OS exit.
        var health = await PdfRestartFacts.ObserveHealthAsync(
            () => rig.PdfHealthAsync(cancellationToken),
            cancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            "the watchdog's trip: the worker's health answered " + health + ".");

        stall.Disarm();
        await rig.StopPdfAsync();
        await rig.StartPdfAsync(cancellationToken);

        var row = await rig.WaitForTerminalAsync(jobId, TripAndRecovery, cancellationToken);
        Assert.Equal("SUCCEEDED", row.Status);
        Assert.Equal("SUCCEEDED", row.OutputStatus);
        Assert.NotNull(row.ArtifactId);
        Assert.NotNull(row.ObjectKey);
        Assert.Equal(1, await rig.CountPartCommitsAsync(Assert.IsType<string>(row.OutputId), cancellationToken));

        var outputId = Assert.IsType<string>(row.OutputId);
        var cause = await rig.Jobs.FindLastAttemptCauseAsync(outputId, 0, cancellationToken);
        Assert.NotNull(cause);
        Assert.Equal(AttemptCauseVocabulary.WatchdogDeadline, cause!.Cause);
        Assert.Equal(1, exit.Calls);
        Assert.Equal(1, metrics.Count("reportservice.pdf.watchdog.exits", ("reason", "deadline")));
        Assert.Contains(rig.Logs.Lines, line => line.Contains("Render watchdog: block 0 of output " + outputId + " past its deadline; stopping", StringComparison.Ordinal));
        Assert.Contains(rig.Logs.Lines, line => line.Contains("Output " + outputId + " ended with RENDER_TIMEOUT", StringComparison.Ordinal));

        var child = await rig.OutputHistoryAsync(jobId, cancellationToken);
        Assert.Equal(2, Started(child, PdfRestartFacts.RenderPdfBlock));
        await PdfRestartFacts.AssertInsideTheBound(child, cancellationToken);
    }

    /// <summary>
    /// The orchestration worker stopped after the part commit and started again: the output is
    /// published once, one artifact row names the one object, and the guards' no-op is the only
    /// thing the resumed step does. A graceful stop is honest here - nothing is in flight on the
    /// render, and what the case proves is publication's idempotency, not a kill.
    /// </summary>
    [Fact]
    public async Task TheOrchestrationWorkerRestartedBeforePublicationPublishesOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            startOrchestration: true,
            cancellationToken: cancellationToken);
        await rig.SeedTakenVersionAsync(PrintingRig.First, cancellationToken);

        var jobId = await SubmitAsync(rig, PrintingRig.First, cancellationToken);
        var outputId = await WaitForOutputIdAsync(rig, jobId, cancellationToken);
        await ReportService.Testing.Wait.UntilAsync(
            "the first part to be committed before the orchestration worker is stopped",
            async token => await rig.CountPartCommitsAsync(outputId, token) == 1,
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);

        await rig.StopOrchestrationAsync();
        await rig.StartOrchestrationAsync(cancellationToken);

        var row = await rig.WaitForTerminalAsync(jobId, Terminal, cancellationToken);
        Assert.Equal("SUCCEEDED", row.Status);
        Assert.Equal(1, await rig.CountPartCommitsAsync(outputId, cancellationToken));
        Assert.NotNull(row.ArtifactId);
        Assert.Equal(1, await rig.CountArtifactsAsync(jobId, cancellationToken));
        await PdfRestartFacts.AssertInsideTheBound(await rig.HistoryAsync(jobId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// A restart with no work in flight: the hosts are stopped before the job's child starts and
    /// the job still proceeds and publishes once; stopped after the job reached its terminal
    /// status, nothing is revived - no second artifact and no second execution of the job.
    /// </summary>
    [Fact]
    public async Task ARestartWithNoWorkInFlightNeitherDuplicatesNorRevives()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(
            _fixture,
            startOrchestration: false,
            startPdf: false,
            cancellationToken: cancellationToken);
        await rig.SeedTakenVersionAsync(PrintingRig.First, cancellationToken);

        // (i) Nothing is running when the job is accepted: the hosts start afterwards.
        var jobId = await SubmitAsync(rig, PrintingRig.First, cancellationToken);
        await rig.StartOrchestrationAsync(cancellationToken);
        await rig.StartPdfAsync(cancellationToken);
        var first = await rig.WaitForTerminalAsync(jobId, Terminal, cancellationToken);
        Assert.Equal("SUCCEEDED", first.Status);
        Assert.Equal(1, await rig.CountArtifactsAsync(jobId, cancellationToken));

        // (ii) Both hosts stop and start again after the terminal status: nothing changes.
        await rig.StopPdfAsync();
        await rig.StopOrchestrationAsync();
        await rig.StartOrchestrationAsync(cancellationToken);
        await rig.StartPdfAsync(cancellationToken);

        var second = await rig.WaitForTerminalAsync(jobId, TimeSpan.FromMinutes(2), cancellationToken);
        Assert.Equal("SUCCEEDED", second.Status);
        Assert.Equal(1, await rig.CountArtifactsAsync(jobId, cancellationToken));
        var bytes = await rig.ReadArtifactAsync(second, cancellationToken);
        Assert.Equal(second.SizeBytes, bytes.LongLength);
        Assert.Equal(second.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        var history = await rig.HistoryAsync(jobId, cancellationToken);

        // One execution was started for the job and one completed: the restart revived nothing.
        Assert.Single(history.Events, entry => entry.EventType == EventType.WorkflowExecutionStarted);
        Assert.Single(history.Events, entry => entry.EventType == EventType.WorkflowExecutionCompleted);
        await PdfRestartFacts.AssertInsideTheBound(history, cancellationToken);
    }

    /// <summary>
    /// The container harness the composition cases build on: it starts the published image, stops
    /// it with the runtime's kill, starts it again, reads the restart policy the runtime reports
    /// and its own log. The composition's container is the Aspire model's and is not reachable
    /// from another test assembly, so this case is where the harness's three members are
    /// exercised rather than left dead.
    /// </summary>
    [Fact]
    public async Task TheContainerHarnessStopsStartsAndReadsThePolicy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;
        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        var engine = await EngineStart.StartAsync(
            CompositionPins.Load().WorkflowEngine,
            "temporal-pdf-restart-harness",
            cancellationToken);
        var root = Path.Combine(
            Path.GetTempPath(),
            "report-service-pdf-restart-harness",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);
        var storage = Path.Combine(root, "storage");
        var source = Path.Combine(root, "template-source");
        try
        {
            Directory.CreateDirectory(storage);
            Directory.CreateDirectory(source);
            await using var worker = await RenderWorkerContainer.StartAsync(
                connectionString,
                engine.HostPort,
                storage,
                source,
                cancellationToken);
            await worker.WaitForHealthyAsync(cancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(worker.Id));

            await worker.StopAsync(cancellationToken);
            await worker.StartAsync(cancellationToken);

            // The image the harness starts is the composition's own image, started without the
            // composition's argument: the runtime reports the default policy here, and the
            // composition's own container is where the SPEC's `always` is read.
            Assert.Equal("no", await worker.RestartPolicyAsync(cancellationToken));
            Assert.False(string.IsNullOrWhiteSpace(await worker.LogsAsync(cancellationToken)));
        }
        finally
        {
            await engine.Container.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }

    // One accepted job over a ready upload of the pilot's own sample, printed from the version
    // the case took.
    private static async Task<string> SubmitAsync(PrintingRig rig, string version, CancellationToken cancellationToken)
    {
        var document = await File.ReadAllBytesAsync(
            Path.Combine(RepositoryPaths.Root, "samples", "templates", "pilot-simple", "sample.json"),
            cancellationToken);
        var uploadId = await rig.ReadyUploadAsync(document, cancellationToken);
        return await rig.SubmitAsync(uploadId, PrintingRig.Template, version, null, null, cancellationToken);
    }

    private static async Task<string> WaitForOutputIdAsync(PrintingRig rig, string jobId, CancellationToken cancellationToken)
    {
        string? outputId = null;
        await ReportService.Testing.Wait.UntilAsync(
            "the job's output to be recorded",
            async token =>
            {
                outputId = (await rig.ReadAsync(jobId, token))?.OutputId;
                return outputId is not null;
            },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);
        return outputId!;
    }

    private static int Started(WorkflowHistory history, string activityName)
    {
        var scheduled = history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled
                && string.Equals(
                    entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                    activityName,
                    StringComparison.Ordinal))
            .Select(entry => entry.EventId)
            .ToHashSet();
        return history.Events.Count(entry =>
            entry.EventType == EventType.ActivityTaskStarted
            && scheduled.Contains(entry.ActivityTaskStartedEventAttributes.ScheduledEventId));
    }

    private static int Scheduled(WorkflowHistory history, string activityName) =>
        history.Events.Count(entry =>
            entry.EventType == EventType.ActivityTaskScheduled
            && string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                activityName,
                StringComparison.Ordinal));
}

/// <summary>
/// The stall a deadline case owns: it waits only when it is armed and only at the phase it was
/// built for, it never observes the cancellation token (a stall that could be cancelled would
/// stop being "neither writes nor returns"), and it counts the attempts it stalled.
/// </summary>
internal sealed class SwitchableStall(RenderPhase phase, TimeSpan span) : IRenderStall
{
    private int _armed = 1;

    private int _calls;

    /// <summary>How many attempts this stall held.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Stops holding the next attempt, so a fresh host's retry can print.</summary>
    public void Disarm() => Volatile.Write(ref _armed, 0);

    /// <inheritdoc />
    public async Task StallAsync(RenderPhase at, CancellationToken cancellationToken)
    {
        if (at != phase || Volatile.Read(ref _armed) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _calls);
        await Task.Delay(span, CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>The exit seam as a recorder: the OS-level exit would end the test process.</summary>
internal sealed class RecordingExit : PdfRendering.IProcessExit
{
    private int _calls;

    /// <summary>How many times the watchdog asked the process to end.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public void Exit(int exitCode) => Interlocked.Increment(ref _calls);
}

/// <summary>What this class's histories and rows are read by, and the bound they stay inside.</summary>
internal static class PdfRestartFacts
{
    /// <summary>The render step's registered name.</summary>
    public const string RenderPdfBlock = "RenderPdfBlock";

    /// <summary>The cause read's registered name.</summary>
    public const string FindLastAttemptCause = "FindLastAttemptCause";

    /// <summary>The publication step's registered name.</summary>
    public const string PublishOutput = "PublishOutput";

    /// <summary>Every history a run of this class writes stays inside the engine's budget.</summary>
    /// <param name="history">The history read.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static Task AssertInsideTheBound(WorkflowHistory history, CancellationToken cancellationToken)
    {
        Assert.True(
            history.Events.Count <= 10_000,
            string.Create(
                CultureInfo.InvariantCulture,
                $"a history of this class holds {history.Events.Count} events, and a run may take at most 10 000."));
        return Task.CompletedTask;
    }

    /// <summary>Reads a health answer and reports it as a word, never as a test failure.</summary>
    /// <param name="read">The read.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task<string> ObserveHealthAsync(Func<Task<HttpStatusCode>> read, CancellationToken cancellationToken)
    {
        try
        {
            return (await read()).ToString();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "no answer (" + exception.GetType().Name + ")";
        }
    }
}
/// <summary>
/// The meters one process emits, captured where the workers run in it: every measurement on the
/// service's own meter name, with its tags, so a case can assert the instruments the spec names.
/// </summary>
internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener;

    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Instrument, IReadOnlyDictionary<string, object?> Tags)> _measures = new();

    /// <summary>Starts listening on the service's meter name.</summary>
    public MetricCapture()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, TelemetryDefaults.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            _measures.Enqueue((
                instrument.Name,
                tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))));
        _listener.Start();
    }

    /// <summary>How many measurements one instrument took with the given tags.</summary>
    /// <param name="instrument">The instrument's name.</param>
    /// <param name="tags">The tag values the measurement must carry.</param>
    public long Count(string instrument, params (string Key, object? Value)[] tags) =>
        _measures.Count(measure =>
            string.Equals(measure.Instrument, instrument, StringComparison.Ordinal)
            && tags.All(tag => measure.Tags.TryGetValue(tag.Key, out var value)
                               && string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), Convert.ToString(tag.Value, CultureInfo.InvariantCulture), StringComparison.Ordinal)));

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();
}