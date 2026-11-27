using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// US-010 AC-7 over the whole local composition: a cancellation in flight is honoured across a
/// restart of the worker processes that carry it. Each entry is one of AC-7's Given instances —
/// the durable <c>cancel-job</c> request not yet delivered (R1), the job workflow winding down
/// (R2), the render worker mid-render (R3), the non-cancellable finalisation in flight (R4) — plus
/// the post-restart checks (R5) and the uncancelled regression (R6).
/// <para>
/// The oracles are the approved US-010 AC-7, accepted ADR-012 §3.9/§3.10/§3.11(1)/§3.13(2),
/// ADR-048 §3.1 R0 and ADR-018 §3.5 item 5, and the predicates the Bolt's approved SPEC fixes
/// (N1–N5, T, O), never the implementation (G06). Every window is frozen by durable state or by a
/// lock the test holds, and asserted <b>while the worker is stopped</b>: a stopped worker cannot
/// deliver, commit or publish, so the window is a fact and never a timing guess. A window that
/// cannot be established fails with its own diagnostic — never a retry to green, never a weaker
/// assertion (SPEC §15).
/// </para>
/// <para>
/// These cases are written in this V-Bounce and <b>run on the <c>e2e</c> level in the Bolt's
/// integration test cycle</b>, after <c>AITL-MEM-Approval</c> and on the baseline that carries
/// US-009.BOLT-002 and US-010.BOLT-002 (SPEC §8.2). Here they must build and pass static checks;
/// they are not run to green in the V-Bounce.
/// </para>
/// </summary>
public sealed class CancelRestartEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    private const string Sample = "pilot-simple";

    private const string Version = "1.0.0";

    private const string SnapshotHash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan SettledDeadline = TimeSpan.FromMinutes(8);

    private static readonly TimeSpan RunningDeadline = TimeSpan.FromMinutes(4);

    private static readonly TimeSpan CancelIntentDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan RowLockDeadline = TimeSpan.FromMinutes(2);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-cancel-restart",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public CancelRestartEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    /// <summary>
    /// R1 — window (i): the cancellation is recorded while the orchestration worker is stopped, so
    /// no <c>cancel-job</c> request can be delivered and no execution exists. After the worker
    /// starts, the request is delivered, the job ends <c>CANCELLED</c>, every output is
    /// <c>CANCELLED</c>, exactly one delivered request and one execution reference exist with none
    /// left pending, nothing was rendered, and no part commit or artifact exists (N1–N5, T).
    /// </summary>
    [Fact]
    public async Task R1_ACancelRecordedWhileTheOrchestrationWorkerIsStoppedIsDeliveredAfterItsRestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);

        // The window: the worker is stopped before anything is seeded, so no start can produce an
        // execution and no cancellation can be delivered.
        await RestartHarness.StopAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);

        var reportJobId = await SeedQueuedJobAsync(world.DataSource, cancellationToken);

        using var cancel = await CancelAsync(world, reportJobId, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        // The frozen state the window requires, read while the worker is down.
        Assert.Equal("CANCELLING", await JobStatusAsync(world.DataSource, reportJobId, cancellationToken));
        var recorded = Assert.Single(await CancelIntentsAsync(world.DataSource, reportJobId, cancellationToken));
        Assert.Equal("PENDING", recorded.Status);
        Assert.Null(recorded.DispatchedAt);
        Assert.Equal(0, await ExecutionReferenceCountAsync(world.DataSource, reportJobId, cancellationToken));
        Assert.Equal(0, await PartCommitCountAsync(world.DataSource, reportJobId, cancellationToken));
        Assert.Equal(0, await ArtifactCountAsync(world.DataSource, reportJobId, cancellationToken));

        await RestartHarness.StartAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);

        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal("CANCELLED", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.ErrorCode);
        var output = Assert.Single(job.Outputs);
        Assert.Equal("CANCELLED", output.Status);
        Assert.Null(output.ArtifactId);

        // N5: the durable request is resumed and has no second effect — one delivered row, no
        // pending one, one execution reference.
        var intents = await WaitForCancelIntentConsumedAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.True(IsCancelIntentConsumed(intents), DescribeIntents(intents));
        Assert.Equal(1, await ExecutionReferenceCountAsync(world.DataSource, reportJobId, cancellationToken));

        // N1–N3: nothing was rendered, committed or published for the job. The tap has held the
        // render worker's log since before the job was seeded, so this is every line the worker
        // wrote for this composition, not a later snapshot of it.
        Assert.Equal(0, await PartCommitCountAsync(world.DataSource, reportJobId, cancellationToken));
        Assert.Equal(0, await ArtifactCountAsync(world.DataSource, reportJobId, cancellationToken));
        Assert.DoesNotContain(world.Logs.Lines, line => NamesOutput(line, output.OutputId));
    }

    /// <summary>
    /// R2 — window (ii) and Phase C: the render worker is stopped first, so the render attempt the
    /// cancellation must wait for cannot acknowledge it; the job is then cancelled and the durable
    /// state proves the wind-down is open. The orchestration worker is killed and restarted inside
    /// that state. The interruption decides nothing: the resumed run either still winds the job
    /// down or closes it <c>CANCELLED</c> when the engine resolves the lost attempt as cancelled —
    /// never <c>FAILED</c>, never left open (SPEC §5 records both engine answers as acceptable).
    /// Once the render worker is back the job is <c>CANCELLED</c>, with nothing committed or
    /// published at or after the request (T, N1, N2).
    /// </summary>
    [Fact]
    public async Task R2_TheOrchestrationWorkerRestartsWhileTheWindDownIsOpenAndTheJobStillEndsCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);
        var template = await TakeTemplateAsync(world.DataSource, "winddown", cancellationToken);

        var reportJobId = await SubmitAsync(world, template, records: 900, outputs: "\"pdf\"", cancellationToken);
        var outputId = await WaitForRunningRenderAsync(world, reportJobId, cancellationToken);

        // Step 2: the render worker dies while its attempt is in flight, so the cancellation below
        // cannot be acknowledged by it and the wind-down cannot complete.
        await RestartHarness.StopAsync(world.App, TestComposition.RenderWorkerName, cancellationToken);

        using var cancel = await CancelAsync(world, reportJobId, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var cancelInstant = await RestartHarness.CancelInstantAsync(world.DataSource, reportJobId, cancellationToken);

        await RestartHarness.WaitForAsync(
            $"job {reportJobId} to be cancelling with a non-terminal output while its render worker is down",
            async token =>
            {
                var state = await ReadJobAsync(world.DataSource, reportJobId, token);
                return string.Equals(state.Status, "CANCELLING", StringComparison.Ordinal)
                    && state.FinishedAt is null
                    && state.Outputs.All(output => output.Status is not ("SUCCEEDED" or "FAILED" or "CANCELLED"));
            },
            SettledDeadline,
            cancellationToken);

        // Step 4: kill the orchestration worker inside the open window; the window is the durable
        // state below, asserted while it is stopped.
        await RestartHarness.StopAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);
        var frozen = await ReadJobAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal("CANCELLING", frozen.Status);
        Assert.Null(frozen.FinishedAt);
        Assert.Equal(0, await ArtifactCountAsync(world.DataSource, reportJobId, cancellationToken));

        await RestartHarness.StartAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);

        // Step 5: after the restart the interruption must not have decided the job. The resumed run
        // either keeps it CANCELLING — the wind-down still waits for the render path, whose lost
        // attempt the engine has not resolved yet — or the engine resolved that attempt as cancelled
        // and the run is closing it CANCELLED. Both engine answers are acceptable to this design
        // (SPEC §5), and §6 Phase A closes the job either way (ADR-048 R0); what the interruption
        // must never do is fail the job, succeed it, or close it as previously unfinished work.
        var resumedStatus = string.Empty;
        await RestartHarness.WaitForAsync(
            $"job {reportJobId} to be winding down or cancelled after the restart",
            async token =>
            {
                resumedStatus = (await ReadJobAsync(world.DataSource, reportJobId, token)).Status;
                return resumedStatus is "CANCELLING" or "CANCELLED";
            },
            SettledDeadline,
            cancellationToken);
        Assert.Contains(resumedStatus, new[] { "CANCELLING", "CANCELLED" });

        // N1/N2 measured from the request instant hold across the interruption.
        Assert.Equal(0, await PartCommitsAfterAsync(world.DataSource, reportJobId, cancelInstant, cancellationToken));
        Assert.Equal(0, await ArtifactsAfterAsync(world.DataSource, reportJobId, cancelInstant, cancellationToken));

        // Step 6: the render worker returns, the interrupted attempt is resolved and the job closes.
        await RestartHarness.StartAsync(world.App, TestComposition.RenderWorkerName, cancellationToken);
        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("CANCELLED", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.ErrorCode);
        var output = Assert.Single(job.Outputs);
        Assert.Equal(outputId, output.OutputId);
        Assert.Equal("CANCELLED", output.Status);

        // N1/N2 measured from the request instant: nothing was committed or published at or after it.
        Assert.Equal(0, await PartCommitsAfterAsync(world.DataSource, reportJobId, cancelInstant, cancellationToken));
        Assert.Equal(0, await ArtifactsAfterAsync(world.DataSource, reportJobId, cancelInstant, cancellationToken));

        // T: one terminal write — the pair does not change on a later read.
        var later = await ReadJobAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal(job.Status, later.Status);
        Assert.Equal(job.FinishedAt, later.FinishedAt);
    }

    /// <summary>
    /// R3 — window (iii) and Phase D: the render worker is killed mid-render, the way the watchdog's
    /// remedy kills it (SIGKILL, the designed crash — not a graceful teardown), while the cancellation
    /// is in flight. The in-flight attempt is lost and cannot acknowledge the cancellation, so the job
    /// stays <c>CANCELLING</c> until the engine resolves the lost attempt after the render heartbeat
    /// timeout; the retried attempt after the harness starts the container again is a retry of work
    /// already begun and commits nothing, and the job still ends <c>CANCELLED</c> with no part commit
    /// or artifact at or after the request and no block above the highest one begun before it
    /// (T, N1–N4).
    /// </summary>
    [Fact]
    public async Task R3_TheRenderWorkerRestartsMidRenderAndNothingIsCommittedAfterTheCancel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);
        var template = await TakeTemplateAsync(world.DataSource, "render", cancellationToken);

        var reportJobId = await SubmitAsync(world, template, records: 900, outputs: "\"pdf\"", cancellationToken);
        var outputId = await WaitForRunningRenderAsync(world, reportJobId, cancellationToken);

        using var cancel = await CancelAsync(world, reportJobId, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var cancelInstant = await RestartHarness.CancelInstantAsync(world.DataSource, reportJobId, cancellationToken);

        // Step 3: the render worker dies the way the watchdog's remedy kills it — an abrupt process
        // death, not a graceful teardown (SPEC §6 Phase D): the container is killed with SIGKILL, so
        // the in-flight attempt is lost and cannot acknowledge the cancellation. The engine resolves
        // a lost attempt only after the render heartbeat timeout (30 s), so the job stays CANCELLING
        // across the kill and the frozen window is observable. A graceful `docker stop` would let the
        // orchestration worker finalise during the teardown (run 261126-2246: the terminal
        // `CANCELLED` was written 1.27 s before the stop returned).
        var killedAt = DateTimeOffset.UtcNow;
        await RestartHarness.KillContainerAsync(world.RenderWorkerContainerName, cancellationToken);
        var beforeRestart = world.Logs.Lines;
        var highestBegun = await HighestBlockBegunBeforeAsync(
            world.DataSource,
            reportJobId,
            outputId,
            cancelInstant,
            beforeRestart,
            cancellationToken);

        var stopped = await ReadJobAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal("CANCELLING", stopped.Status);
        Assert.Null(stopped.FinishedAt);

        // Step 4: the harness starts the killed render worker through the composition's own
        // `StartCommand` on the declared resource and waits for `Running` — the operator's act, since
        // the local substrate replaces nothing (SPEC revision 3, §6 Phase D, D8/D13). The started
        // container must be the composition's own and its start instant must have advanced past the
        // kill, so the re-established stream carries the restarted process's lines and none from the
        // dead one (§5's obligation, §15's stop). The tap is re-established at that instant, and the
        // wait is the proof that stream is live — without a line written after the kill, the "no
        // block above …" assertion would pass on an empty read (SPEC §6 B.1, §8.2).
        await RestartHarness.StartAsync(world.App, TestComposition.RenderWorkerName, cancellationToken);
        var startedAgainAt = await RestartHarness.StartedAtAsync(world.RenderWorkerContainerName, cancellationToken)
            ?? DateTimeOffset.UtcNow;
        Assert.True(
            startedAgainAt > killedAt,
            $"the render worker container '{world.RenderWorkerContainerName}' reports State.StartedAt {startedAgainAt:O}, which did not advance past the kill at {killedAt:O}: the restarted process's lines cannot be separated from the dead one's");
        var restartBoundary = await world.Logs.ResumeAsync(startedAgainAt, cancellationToken);
        await world.Logs.WaitForLineAfterAsync(
            restartBoundary,
            "a line written after the render worker was stopped",
            cancellationToken);
        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("CANCELLED", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.ErrorCode);
        var output = Assert.Single(job.Outputs);
        Assert.Equal("CANCELLED", output.Status);
        Assert.Null(output.ArtifactId);

        // N1/N2: nothing committed or published at or after the request.
        Assert.Equal(0, await PartCommitsAfterAsync(world.DataSource, reportJobId, cancelInstant, cancellationToken));
        Assert.Equal(0, await ArtifactsAfterAsync(world.DataSource, reportJobId, cancelInstant, cancellationToken));

        // N4: no block above the highest one begun before the request, neither durably nor in the
        // restarted process's log. (On the single-block shape this is block 0, and a retry of it is
        // a retry of work already begun, never a new block — SPEC §7 N4.)
        Assert.Equal(
            0,
            await BlocksAboveAsync(world.DataSource, reportJobId, highestBegun, cancellationToken));
        Assert.DoesNotContain(
            world.Logs.Since(restartBoundary),
            line => NamesOutput(line, outputId) && BlockIndexIn(line, outputId) > highestBegun);

        // T: one terminal write, unchanged on a later read.
        var later = await ReadJobAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal(job.Status, later.Status);
        Assert.Equal(job.FinishedAt, later.FinishedAt);
    }

    /// <summary>
    /// R4 — window (iv) and Phase E: the job is <c>CANCELLING</c> before its execution exists, the
    /// test holds the job's row lock, and the real dispatcher's run reaches the non-cancellable
    /// finalisation and blocks inside it (a second session waits on the row). The orchestration
    /// worker is killed inside that window; after the lock is released and the worker restarts, the
    /// terminal <c>CANCELLED</c> is written once and cleanly (T, R0).
    /// </summary>
    [Fact]
    public async Task R4_TheRestartLandsDuringTheNonCancellableFinalisationAndTheTerminalIsWrittenOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);

        // The job is prepared while no worker can dispatch it.
        await RestartHarness.StopAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);
        var (reportJobId, outputId) = await SeedCancellingBeforeStartAsync(world.DataSource, cancellationToken);

        // The window: the test's own transaction holds the job's row, so the finalisation's
        // SELECT … FOR UPDATE cannot pass it.
        await using var holder = await world.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await holder.BeginTransactionAsync(cancellationToken);
        await using (var command = holder.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT status FROM report_service.report_jobs WHERE report_job_id = @id FOR UPDATE";
            command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
            Assert.Equal("CANCELLING", (string)(await command.ExecuteScalarAsync(cancellationToken))!);
        }

        await RestartHarness.StartAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);
        await WaitForRowLockWaiterAsync(world.DataSource, cancellationToken);

        // Step 5: the attempt is lost while blocked; the job is frozen exactly as the window says.
        await RestartHarness.StopAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);
        var frozen = await ReadJobAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal("CANCELLING", frozen.Status);
        Assert.Null(frozen.FinishedAt);
        var frozenOutput = Assert.Single(frozen.Outputs);
        Assert.Equal("PENDING", frozenOutput.Status);

        // Step 6: release the lock, then let the worker return.
        await transaction.RollbackAsync(cancellationToken);
        await RestartHarness.StartAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);

        // Step 7: one clean terminal write; a later read does not change it (a repeat is
        // AlreadyTerminal, so nothing was written a second time).
        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal("CANCELLED", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.ErrorCode);
        var output = Assert.Single(job.Outputs);
        Assert.Equal(outputId, output.OutputId);
        Assert.Equal("CANCELLED", output.Status);
        Assert.Equal(0, await ArtifactCountAsync(world.DataSource, reportJobId, cancellationToken));

        var later = await ReadJobAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal(job.Status, later.Status);
        Assert.Equal(job.FinishedAt, later.FinishedAt);
        Assert.Equal(output.Status, Assert.Single(later.Outputs).Status);
    }

    /// <summary>
    /// R5 — Phase F.1–F.2, the post-restart checks (O): after a cancellation whose render worker was
    /// restarted mid-render, the worker answers <c>/health</c> and renders a following job, the
    /// cancelled job left no attempt cause or part commit beyond its pre-cancel state, and the
    /// restarted process's log names no block that had not begun before the request.
    /// <para>
    /// What this entry cannot see, stated rather than implied (SPEC §8.2): a Chromium child process
    /// of the killed attempt (it dies with the container — the platform boundary is the reaping
    /// boundary, ADR-012 §3.9(3)), the in-process temporary directory of that attempt (inside the
    /// container; its cleanup is BOLT-002's hermetic cancelled-render case and the resumed attempt's
    /// own <c>finally</c>), and an object written by an attempt the commit guard refused (an orphan
    /// for PROC-009, outside this Bolt). This entry proves the boundary a consumer observes: a
    /// healthy worker, a freed render slot and a following job that publishes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task R5_AfterTheRestartTheWorkerIsHealthyRendersAFollowingJobAndLeavesNoOrphan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);
        var template = await TakeTemplateAsync(world.DataSource, "orphan", cancellationToken);

        var reportJobId = await SubmitAsync(world, template, records: 900, outputs: "\"pdf\"", cancellationToken);
        var outputId = await WaitForRunningRenderAsync(world, reportJobId, cancellationToken);

        using var cancel = await CancelAsync(world, reportJobId, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var cancelInstant = await RestartHarness.CancelInstantAsync(world.DataSource, reportJobId, cancellationToken);

        await RestartHarness.StopAsync(world.App, TestComposition.RenderWorkerName, cancellationToken);
        var beforeRestart = world.Logs.Lines;
        var highestBegun = await HighestBlockBegunBeforeAsync(
            world.DataSource,
            reportJobId,
            outputId,
            cancelInstant,
            beforeRestart,
            cancellationToken);
        var causesBefore = await CauseCountAsync(world.DataSource, reportJobId, cancellationToken);

        await RestartHarness.StartAsync(world.App, TestComposition.RenderWorkerName, cancellationToken);
        await RestartHarness.WaitUntilHealthyAsync(world.App, TestComposition.RenderWorkerName, cancellationToken);

        // The stream is re-established at the restarted container's own start instant: only its
        // lines enter after the boundary, and the wait proves the stream is live before the
        // no-new-work predicate reads it (SPEC §6 B.1, §8.2).
        var restartBoundary = await world.Logs.ResumeAsync(DateTimeOffset.UtcNow, cancellationToken);
        await world.Logs.WaitForLineAfterAsync(
            restartBoundary,
            "a line written after the render worker was stopped",
            cancellationToken);

        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.Equal("CANCELLED", job.Status);
        Assert.NotNull(job.FinishedAt);

        // O: the restarted worker serves its health endpoint and renders a following job, so no
        // render slot, browser or temporary directory of the killed attempt survived it.
        using var health = world.App.CreateHttpClient(TestComposition.RenderWorkerName);
        using var answer = await health.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        var followUpId = await SubmitAsync(world, template, records: 10, outputs: "\"pdf\"", cancellationToken);
        var followUp = await WaitForTerminalAsync(world.DataSource, followUpId, cancellationToken);
        Assert.Equal("SUCCEEDED", followUp.Status);
        Assert.NotNull(Assert.Single(followUp.Outputs).ArtifactId);

        // O/N4: no cause and no part commit of the cancelled job beyond what the pre-cancel state
        // held, and no block index above the highest one that had begun.
        Assert.Equal(causesBefore, await CauseCountAsync(world.DataSource, reportJobId, cancellationToken));
        Assert.Equal(
            0,
            await BlocksAboveAsync(world.DataSource, reportJobId, highestBegun, cancellationToken));
        Assert.Equal(0, await PartCommitsAfterAsync(world.DataSource, reportJobId, cancelInstant, cancellationToken));
        Assert.DoesNotContain(
            world.Logs.Since(restartBoundary),
            line => NamesOutput(line, outputId) && BlockIndexIn(line, outputId) > highestBegun);
    }

    /// <summary>
    /// R6 — Phase F.3, the uncancelled regression: on a composition whose two workers were stopped
    /// and started once, a normal small job and a two-output job both reach their normal terminal
    /// status and publish. The restart mechanism and the new failure-path guard change nothing on
    /// the ordinary path, and no cancellation is recorded for either job.
    /// </summary>
    [Fact]
    public async Task R6_ACompositionWhoseWorkersRestartedStillRunsAndPublishesUncancelledJobs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);
        var template = await TakeTemplateAsync(world.DataSource, "regression", cancellationToken);

        // The same restart mechanism, no cancellation: both workers are stopped and started once.
        await RestartHarness.StopAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);
        await RestartHarness.StopAsync(world.App, TestComposition.RenderWorkerName, cancellationToken);
        await RestartHarness.StartAsync(world.App, TestComposition.RenderWorkerName, cancellationToken);
        await RestartHarness.StartAsync(world.App, RestartHarness.OrchestrationWorkerName, cancellationToken);

        var smallId = await SubmitAsync(world, template, records: 25, outputs: "\"pdf\"", cancellationToken);
        var small = await WaitForTerminalAsync(world.DataSource, smallId, cancellationToken);
        Assert.Equal("SUCCEEDED", small.Status);
        var pdf = Assert.Single(small.Outputs);
        Assert.Equal("SUCCEEDED", pdf.Status);
        Assert.NotNull(pdf.ArtifactId);

        var twoOutputId = await SubmitAsync(world, template, records: 25, outputs: "\"pdf\",\"html\"", cancellationToken);
        var twoOutput = await WaitForTerminalAsync(world.DataSource, twoOutputId, cancellationToken);
        Assert.Equal("SUCCEEDED", twoOutput.Status);
        Assert.Null(twoOutput.ErrorCode);
        Assert.All(twoOutput.Outputs, output => Assert.Equal("SUCCEEDED", output.Status));
        Assert.All(twoOutput.Outputs, output => Assert.NotNull(output.ArtifactId));

        // The ordinary path never asks for a cancellation: nothing was recorded for either job.
        Assert.Equal(0, await CancelIntentCountAsync(world.DataSource, smallId, cancellationToken));
        Assert.Equal(0, await CancelIntentCountAsync(world.DataSource, twoOutputId, cancellationToken));
    }

    // ---- composition and fixtures ----

    private async Task<World> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);

        var builder = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");

        // The composition declares the render worker's container name in its own model; the name is
        // read back before the model is built, so the tap addresses exactly this composition's
        // container and never a listing of the daemon (SPEC §6 B.1, the BUG-057 pattern).
        var renderWorkerContainerName = TestComposition.DeclaredRenderWorkerContainerName(builder);
        var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        // The render worker's log is tapped here, from the container runtime's own stream, before
        // any job is submitted: its attempt's "started" line is written once and must be observed
        // rather than re-read later.
        var logs = await RenderLogTap.OpenAsync(renderWorkerContainerName, cancellationToken);

        using (var health = app.CreateHttpClient("api"))
        {
            await Wait.UntilAsync(
                "the API answering /health",
                async token =>
                {
                    using var response = await health.GetAsync(new Uri("/health", UriKind.Relative), token);
                    return response.StatusCode == HttpStatusCode.OK;
                },
                HealthyDeadline,
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }

        var connectionString = await app.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        var dataSource = NpgsqlDataSource.Create(connectionString);
        return new World(app, dataSource, app.CreateHttpClient("api"), logs, renderWorkerContainerName);
    }

    // Takes the fixture package into the composition's shared storage and metadata, the way a
    // second job over an already-taken version sees it — the render worker container reads no
    // template source.
    private static async Task<string> TakeTemplateAsync(
        NpgsqlDataSource dataSource,
        string suffix,
        CancellationToken cancellationToken)
    {
        var template = Sample + "-restart-" + suffix;
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var versions = new PostgresTemplateVersionStore(dataSource);

        var files = SamplePackage();
        if (files.TryGetValue("template.yaml", out var manifest))
        {
            var text = Encoding.UTF8.GetString(manifest).Replace("id: " + Sample, "id: " + template, StringComparison.Ordinal);
            files["template.yaml"] = Encoding.UTF8.GetBytes(text);
        }

        var source = TemplateSourceClass.Over(storage);
        await source.RemoveAsync(template, Version);
        await source.PublishAsync(template, Version, files);

        var options = new TemplateSourceOptions();
        var take = new TemplateTake(
            new StorageTemplateSource(storage, options, new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance),
            versions,
            storage,
            new TemplateManifestReader(),
            new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance),
            [],
            new TemplateTakeMetrics(),
            TimeProvider.System,
            Options.Create(options),
            NullLogger<TemplateTake>.Instance);
        var outcome = await take.TakeAsync(template, Version, Consumer, reportJobId: null, cancellationToken);
        Assert.NotNull(outcome.Record);
        return template;
    }

    private static Dictionary<string, byte[]> SamplePackage()
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);
            if (string.Equals(relative, "template.yaml", StringComparison.Ordinal))
            {
                text = text.Replace("owner: dev-consumer", "owner: " + Consumer, StringComparison.Ordinal);
            }

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        return files;
    }

    private static async Task<string> SubmitAsync(
        World world,
        string template,
        int records,
        string outputs,
        CancellationToken cancellationToken)
    {
        var snapshot = Snapshot(records);
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var uploads = new PostgresUploadStore(world.DataSource);
        var uploadId = await ReadyUploadAsync(uploads, storage, snapshot, cancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                "{\"template\":{\"id\":\"" + template + "\",\"version\":\"" + Version + "\"},"
                + "\"data\":{\"uploadId\":\"" + uploadId + "\",\"profile\":\"header-items-v1\"},\"outputs\":[" + outputs + "]}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await world.Api.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    // A ready upload of the snapshot, written through the composition's own records and storage.
    private static async Task<string> ReadyUploadAsync(
        PostgresUploadStore uploads,
        IObjectStore storage,
        byte[] snapshot,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(1)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            snapshot.LongLength,
            snapshot.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(snapshot)),
            now,
            cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(1), cancellationToken));

        var key = ObjectKey.FromSegments("snapshots", uploadId, "a");
        using var content = new MemoryStream(snapshot, writable: false);
        var written = await storage.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    key,
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    written.Value!.Sha256,
                    snapshot.LongLength,
                    written.Value.Sha256,
                    written.Value.Size,
                    now),
                now,
                cancellationToken));
        return uploadId;
    }

    // An upload that is confirmed but not committed-ready: the job over it is accepted and stays
    // non-terminal, so a seeded job can wait for its cancellation without any render path.
    private static async Task<string> VerifyingUploadAsync(
        PostgresUploadStore uploads,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(2)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(uploadId, UploadContentOutcome.Complete, 900, 800, SnapshotHash, now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(2), cancellationToken));
        return uploadId;
    }

    // A QUEUED job over a verifying upload, seeded through the composition's own stores: its
    // start-job request is recorded and pending, and no execution exists while no worker runs.
    private static async Task<string> SeedQueuedJobAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var uploads = new PostgresUploadStore(dataSource);
        var jobs = new PostgresJobStore(dataSource);
        var uploadId = await VerifyingUploadAsync(uploads, cancellationToken);

        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
            Consumer,
            uploadId,
            Sample,
            TemplateReferenceForm.Exact,
            Version,
            AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await jobs.AcceptAsync(acceptance, DateTimeOffset.UtcNow, cancellationToken));
        return acceptance.ReportJobId;
    }

    // The BOLT-002 A4 shape, deterministically: a job that is CANCELLING before its execution
    // exists — accepted (which records its start-job request) and cancelled (which records the
    // cancel-job request) while no worker can dispatch either.
    private static async Task<(string ReportJobId, string OutputId)> SeedCancellingBeforeStartAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var uploads = new PostgresUploadStore(dataSource);
        var jobs = new PostgresJobStore(dataSource);
        var uploadId = await VerifyingUploadAsync(uploads, cancellationToken);

        var outputId = JobIdentifier.CreateOutput();
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(outputId, OutputFormat.Pdf)],
            Consumer,
            uploadId,
            Sample,
            TemplateReferenceForm.Exact,
            Version,
            AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await jobs.AcceptAsync(acceptance, DateTimeOffset.UtcNow, cancellationToken));
        Assert.IsType<CancellationRequestResult.Transitioned>(
            await jobs.RequestCancellationAsync(acceptance.ReportJobId, Consumer, DateTimeOffset.UtcNow, cancellationToken));
        return (acceptance.ReportJobId, outputId);
    }

    private static byte[] Snapshot(int records)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample, "sample.json")))!;
        var items = sample["items"]!.AsArray();
        var built = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(items[index % items.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;
            built.Add(item);
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = records;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    // ---- driving and reading the real composition ----

    private static Task<HttpResponseMessage> CancelAsync(World world, string reportJobId, CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}/cancel", UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return world.Api.SendAsync(message, cancellationToken);
    }

    // Submits the long-render job and waits until its PDF output runs and the render worker's log
    // names the attempt it started: the entry may stop the worker only inside that state, so the
    // window is the worker's own line and not a timing guess.
    private static async Task<string> WaitForRunningRenderAsync(
        World world,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        string? outputId = null;
        await RestartHarness.WaitForAsync(
            $"job {reportJobId} to run its PDF output",
            async token =>
            {
                var job = await ReadJobAsync(world.DataSource, reportJobId, token);
                var pdf = job.Outputs.FirstOrDefault(output => output.Format == "PDF");
                outputId = pdf?.OutputId;
                return pdf is { Status: "RUNNING" } && outputId is not null;
            },
            RunningDeadline,
            cancellationToken);

        var id = outputId!;
        await world.Logs.WaitForAsync(
            $"the first render attempt of output {id}",
            line => NamesOutput(line, id) && line.Contains("started, attempt", StringComparison.Ordinal),
            cancellationToken);
        return id;
    }

    // Polls the real PostgreSQL until the job reaches a terminal status and reads it with every
    // output and the identifier of its published artifact.
    private static async Task<SettledJob> WaitForTerminalAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        SettledJob? job = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to settle",
            async token =>
            {
                job = await ReadJobAsync(dataSource, reportJobId, token);
                return job.Status is "SUCCEEDED" or "FAILED" or "CANCELLED" or "PARTIALLY_SUCCEEDED";
            },
            SettledDeadline,
            TimeSpan.FromMilliseconds(300),
            cancellationToken);
        return job!;
    }

    private static async Task<string> JobStatusAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM report_service.report_jobs WHERE report_job_id = @id";
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<SettledJob> ReadJobAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT j.status, j.error_code, j.finished_at,
                   o.output_id, o.format, o.status, o.error_code, o.finished_at,
                   a.artifact_id
              FROM report_service.report_jobs j
              LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
              LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
             WHERE j.report_job_id = @id
             ORDER BY o.format
            """;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var status = string.Empty;
        string? errorCode = null;
        DateTime? finishedAt = null;
        var outputs = new List<SettledOutput>();
        while (await reader.ReadAsync(cancellationToken))
        {
            status = reader.GetString(0);
            errorCode = reader.IsDBNull(1) ? null : reader.GetString(1);
            finishedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            if (!reader.IsDBNull(3))
            {
                outputs.Add(new SettledOutput(
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        return new SettledJob(status, errorCode, finishedAt, outputs);
    }

    private static Task<int> ArtifactCountAsync(NpgsqlDataSource dataSource, string reportJobId, CancellationToken cancellationToken) =>
        ScalarAsync(dataSource, "SELECT count(*) FROM report_service.artifacts WHERE report_job_id = @id", reportJobId, cancellationToken);

    private static Task<int> PartCommitCountAsync(NpgsqlDataSource dataSource, string reportJobId, CancellationToken cancellationToken) =>
        ScalarAsync(
            dataSource,
            """
            SELECT count(*)
              FROM report_service.pdf_part_commits pc
              JOIN report_service.report_outputs o ON o.output_id = pc.output_id
             WHERE o.report_job_id = @id
            """,
            reportJobId,
            cancellationToken);

    private static async Task<int> ScalarAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> PartCommitsAfterAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        DateTimeOffset instant,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
              FROM report_service.pdf_part_commits pc
              JOIN report_service.report_outputs o ON o.output_id = pc.output_id
             WHERE o.report_job_id = @id AND pc.committed_at >= @instant
            """;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        command.Parameters.Add(new NpgsqlParameter("instant", instant.UtcDateTime));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ArtifactsAfterAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        DateTimeOffset instant,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM report_service.artifacts WHERE report_job_id = @id AND created_at >= @instant";
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        command.Parameters.Add(new NpgsqlParameter("instant", instant.UtcDateTime));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static Task<int> CauseCountAsync(NpgsqlDataSource dataSource, string reportJobId, CancellationToken cancellationToken) =>
        ScalarAsync(
            dataSource,
            """
            SELECT count(*)
              FROM report_service.pdf_attempt_causes c
              JOIN report_service.report_outputs o ON o.output_id = c.output_id
             WHERE o.report_job_id = @id
            """,
            reportJobId,
            cancellationToken);

    private static async Task<int> BlocksAboveAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        int highestBegun,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
              FROM (
                    SELECT pc.block_index
                      FROM report_service.pdf_part_commits pc
                      JOIN report_service.report_outputs o ON o.output_id = pc.output_id
                     WHERE o.report_job_id = @id
                    UNION ALL
                    SELECT c.block_index
                      FROM report_service.pdf_attempt_causes c
                      JOIN report_service.report_outputs o ON o.output_id = c.output_id
                     WHERE o.report_job_id = @id
                   ) blocks
             WHERE blocks.block_index > @highest
            """;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        command.Parameters.Add(new NpgsqlParameter("highest", highestBegun));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    // The highest block that had begun before the request instant: durable work recorded before it
    // (a part commit or an attempt cause), or a render line the log held before the window was
    // closed. An output with no such work answers -1: nothing had begun for it.
    private static async Task<int> HighestBlockBegunBeforeAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        string outputId,
        DateTimeOffset instant,
        IReadOnlyList<string> logLines,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT coalesce(max(block_index), -1)
              FROM (
                    SELECT pc.block_index
                      FROM report_service.pdf_part_commits pc
                      JOIN report_service.report_outputs o ON o.output_id = pc.output_id
                     WHERE o.report_job_id = @id AND pc.committed_at < @instant
                    UNION ALL
                    SELECT c.block_index
                      FROM report_service.pdf_attempt_causes c
                      JOIN report_service.report_outputs o ON o.output_id = c.output_id
                     WHERE o.report_job_id = @id AND c.recorded_at < @instant
                   ) begun
            """;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        command.Parameters.Add(new NpgsqlParameter("instant", instant.UtcDateTime));
        var durable = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        var logged = logLines
            .Where(line => NamesOutput(line, outputId))
            .Select(line => BlockIndexIn(line, outputId))
            .DefaultIfEmpty(-1)
            .Max();

        return Math.Max(durable, logged);
    }

    private static async Task<int> CancelIntentCountAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken) =>
        (await CancelIntentsAsync(dataSource, reportJobId, cancellationToken)).Count;

    private static async Task<int> ExecutionReferenceCountAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
              FROM report_service.execution_references r
              JOIN report_service.intents i ON i.intent_id = r.intent_id
             WHERE i.kind = @kind AND i.subject_id = @id
            """;
        command.Parameters.Add(new NpgsqlParameter("kind", RestartHarness.CancelJobKind));
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    // Waits, bounded, until the cancel-job request of the job is consumed: exactly one row for the
    // job, DISPATCHED with its instant, and none still PENDING (SPEC §7 N5). The dispatcher's poll is
    // asynchronous and independent of the workflow that closes the job, so a terminal job never
    // implies a delivered request; on timeout the failure names the last status observed.
    private static async Task<IReadOnlyList<CancelIntent>> WaitForCancelIntentConsumedAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CancelIntent> observed = [];
        await Wait.UntilAsync(
            $"the cancel-job request of {reportJobId} to be consumed",
            async token =>
            {
                observed = await CancelIntentsAsync(dataSource, reportJobId, token);
                return IsCancelIntentConsumed(observed);
            },
            CancelIntentDeadline,
            RestartHarness.PollInterval,
            cancellationToken);
        return observed;
    }

    private static bool IsCancelIntentConsumed(IReadOnlyList<CancelIntent> intents) =>
        intents.Count == 1
        && string.Equals(intents[0].Status, "DISPATCHED", StringComparison.Ordinal)
        && intents[0].DispatchedAt is not null
        && !intents.Any(intent => string.Equals(intent.Status, "PENDING", StringComparison.Ordinal));

    private static string DescribeIntents(IReadOnlyList<CancelIntent> intents) =>
        intents.Count == 0
            ? "no cancel-job row"
            : string.Join(
                ", ",
                intents.Select(intent => intent.DispatchedAt is { } instant
                    ? intent.Status + " at " + instant.ToString("O", CultureInfo.InvariantCulture)
                    : intent.Status + " without dispatched_at"));

    private static async Task<IReadOnlyList<CancelIntent>> CancelIntentsAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT status, dispatched_at
              FROM report_service.intents
             WHERE kind = @kind AND subject_id = @id
             ORDER BY intent_id
            """;
        command.Parameters.Add(new NpgsqlParameter("kind", RestartHarness.CancelJobKind));
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var intents = new List<CancelIntent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            intents.Add(new CancelIntent(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), TimeSpan.Zero)));
        }

        return intents;
    }

    // The window of R4, as a fact: a session other than this one waits on a lock while its statement
    // is the job's own guarded read — the finalisation blocked inside its transaction on the row the
    // test holds (SPEC §6 Phase E, §14 D9).
    private static async Task WaitForRowLockWaiterAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await RestartHarness.WaitForAsync(
            "a second session to wait on a row lock while its statement is the job's guarded read",
            async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT count(*)
                      FROM pg_stat_activity
                     WHERE pid <> pg_backend_pid()
                       AND datname = current_database()
                       AND wait_event_type = 'Lock'
                       AND query LIKE '%report_service.report_jobs%'
                       AND query LIKE '%FOR UPDATE%'
                    """;
                return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) > 0;
            },
            RowLockDeadline,
            cancellationToken);
    }

    private static bool NamesOutput(string line, string outputId) =>
        line.Contains("of output " + outputId, StringComparison.Ordinal);

    // The block index a render line names for the output, parsed from its own text; a line that
    // names no index answers -1 and is never the maximum.
    private static int BlockIndexIn(string line, string outputId)
    {
        var marker = "Block ";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0 || !NamesOutput(line, outputId))
        {
            return -1;
        }

        var start = index + marker.Length;
        var length = 0;
        while (start + length < line.Length && char.IsAsciiDigit(line[start + length]))
        {
            length++;
        }

        return length > 0 && int.TryParse(line.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out var block)
            ? block
            : -1;
    }

    private sealed record World(
        DistributedApplication App,
        NpgsqlDataSource DataSource,
        HttpClient Api,
        RenderLogTap Logs,
        string RenderWorkerContainerName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Api.Dispose();
            await DataSource.DisposeAsync();
            await Logs.DisposeAsync();
            await App.DisposeAsync();
        }
    }

    private sealed record SettledJob(
        string Status,
        string? ErrorCode,
        DateTime? FinishedAt,
        IReadOnlyList<SettledOutput> Outputs);

    private sealed record SettledOutput(
        string OutputId,
        string Format,
        string Status,
        string? ErrorCode,
        DateTime? FinishedAt,
        string? ArtifactId);

    private sealed record CancelIntent(string Status, DateTimeOffset? DispatchedAt);
}
