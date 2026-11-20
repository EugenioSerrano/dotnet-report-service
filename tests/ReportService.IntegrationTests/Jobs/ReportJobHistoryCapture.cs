extern alias orchestration;
extern alias pdf;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.DataContract;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Resolution;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;
using InputWaitActivities = orchestration::ReportService.Workers.Orchestration.Jobs.InputWaitActivities;
using JobTransitionActivities = orchestration::ReportService.Workers.Orchestration.Jobs.JobTransitionActivities;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;
using PdfOutputActivities = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivities;
using ReportJobActivities = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobActivities;
using ReportJobInput = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobInput;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;
using RetentionOptions = orchestration::ReportService.Workers.Orchestration.Retention.RetentionOptions;
using TemplateResolutionActivities = orchestration::ReportService.Workers.Orchestration.Jobs.TemplateResolutionActivities;
using ValidateAndPartitionActivity = orchestration::ReportService.Workers.Orchestration.Jobs.ValidateAndPartitionActivity;
using PdfOutputActivityOptions = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivityOptions;
using ResolveTemplateVersionActivity = pdf::ReportService.Workers.Pdf.Templates.ResolveTemplateVersionActivity;
using TakeTemplateVersionActivity = pdf::ReportService.Workers.Pdf.Templates.TakeTemplateVersionActivity;
using TemplateResolutionActivityOptions = pdf::ReportService.Workers.Pdf.Templates.TemplateResolutionActivityOptions;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// Records the job histories the hermetic suite replays and this build can reproduce, once, so
/// that suite has something to check. It is opt-in: it runs only when the environment variable
/// names the directory to write, so the integration command never runs it, and the ordinary
/// cases of this suite are untouched by it. It needs one engine and nothing else — the jobs it
/// runs live in the in-memory record stores and a storage root of its own — so every history it
/// writes is the outcome of a real execution of the workflow and its steps, taken under the very
/// settings the case that replays it replays it under. The data step runs for real: the records
/// name a taken version whose package and schema are written behind them, and every snapshot
/// document is written before the record that carries its size and SHA-256.
/// <para>
/// The recordings whose runs need steps this rig does not serve are kept as captured and
/// replayed under their own settings; each capture case below names what it records and why the
/// rest is not re-recorded.
/// </para>
/// </summary>
public sealed class ReportJobHistoryCapture
{
    /// <summary>The variable that names the directory the captured histories are written to; without it the case is skipped.</summary>
    public const string OutputVariable = "REPORTSERVICE_CAPTURE_WORKFLOW_HISTORY";

    /// <summary>How long one recorded execution, or the wait for it to reach a point, may take.</summary>
    private static readonly TimeSpan ExecutionDeadline = TimeSpan.FromSeconds(60);

    /// <summary>The instant the records of the first recording are written with.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    /// <summary>The consumer every recorded job is accepted for.</summary>
    private const string Consumer = "dev-consumer";

    /// <summary>The template every recorded job names, at the version whose package the rig holds.</summary>
    private const string TemplateId = "pilot-grouped";

    /// <summary>The version whose package and schema the rig holds: the pilot's own.</summary>
    private const string Version = "1.0.0";

    /// <summary>The version whose package carries a schema outside the profile.</summary>
    private const string RefusedVersion = "1.1.0";

    /// <summary>The path of the schema inside every package the rig writes.</summary>
    private const string SchemaPath = "schema.json";

    /// <summary>The states a recorded job's upload is in when its execution starts.</summary>
    private enum UploadState
    {
        Ready,
        Rejected,
        Verifying,
    }

    /// <summary>What ends the wait of a recorded job while it waits.</summary>
    private enum UploadEnding
    {
        Ready,
        Rejected,
        Expired,
    }

    /// <summary>
    /// Records the reachable histories of a job that ends at its input check or inside its
    /// wait: a job whose upload had been rejected at its first check, the two ways a
    /// notification ends the wait, and the run that continues as a new one while it waits.
    /// <para>
    /// The other recordings the hermetic suite replays are <b>not</b> recorded here: a fresh
    /// execution of this build schedules steps this rig does not serve — the template step, and
    /// the printing path whose output child and renderer no capture rig registers — so those
    /// recordings are kept as captured and replayed under the settings they were taken with.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheReachableJobHistoriesAreRecorded()
    {
        var target = Target();
        await using var rig = await Rig.StartAsync();

        await RecordTheFirstCheckAsync(rig, JobHistoryFixtures.Rejected, UploadState.Rejected, target);
        await RecordTheNotificationEndingTheWaitAsync(
            rig, JobHistoryFixtures.WaitingThenRejected, UploadEnding.Rejected, target);
        await RecordTheNotificationEndingTheWaitAsync(
            rig, JobHistoryFixtures.WaitingThenExpired, UploadEnding.Expired, target);
        await RecordTheRunThatContinuedAsync(rig, JobHistoryFixtures.ContinuedAsNew, target);
    }

    /// <summary>
    /// Records the one history of the job's template step that a fresh execution can reproduce:
    /// a take that refuses the version it was given, which ends the job with the take's code and
    /// pins nothing. It is opt-in like the whole capture, and it runs the rig that serves the
    /// template step's metadata activities and the take on its own queue.
    /// <para>
    /// The other recordings of the step are <b>not</b> recorded here: their runs continue past
    /// the step into the printing path, whose output child and renderer no capture rig registers,
    /// so they are kept as captured and replayed under the settings they were taken with.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheTakeRefusalHistoryIsRecorded()
    {
        var target = Target();
        await using var rig = await Rig.StartAsync(withTemplateStep: true);

        await RecordTheVersionTheTakeRefusesAsync(rig, JobHistoryFixtures.TemplateTakeRefused, target);
    }

    /// <summary>
    /// Records the one history of a PDF output the row bound refused: a document whose second
    /// record carries a description of 262 145 ASCII bytes, a data step that commits the
    /// refusal with the partition and returns it, and a workflow that moves the job to running,
    /// looks its output up and ends that output with the code and the reason the pass kept,
    /// starting no child for it and rendering nothing. It is opt-in like the whole capture, and
    /// it runs a rig that also serves the output lookup on the orchestration queue.
    /// <para>
    /// The recordings the earlier changes captured are <b>not</b> re-captured here: each was
    /// taken before the steps this build carries, so a fresh execution of the current workflow
    /// could not reproduce them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRowBoundRefusalHistoryIsRecorded()
    {
        var target = Target();
        await using var rig = await Rig.StartAsync(withTemplateStep: true, withPdfOutput: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(
            JobHistoryFixtures.RowBoundRefused,
            UploadState.Ready,
            cancellationToken,
            rig.RowBoundDocument());
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(
            directory: target,
            JobHistoryFixtures.RowBoundRefused,
            await rig.HistoryAsync(reportJobId, runId, cancellationToken),
            cancellationToken);
    }

    // A job that named a version already taken: the step binds it, the source is never read,
    // and the run continues through the data step and the finalising step as it always does.
    private static async Task RecordTheBoundVersionAsync(Rig rig, JobHistoryFixture fixture, string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(
            fixture, UploadState.Ready, cancellationToken, version: Rig.SeededVersion);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    // A job that named a version not taken: the take reads it on the PDF worker's queue and
    // the step pins what the take answered.
    private static async Task RecordTheTakenVersionAsync(Rig rig, JobHistoryFixture fixture, string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(
            fixture, UploadState.Ready, cancellationToken, version: Rig.SmallestUntakenVersion);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    // A job that named only its template: the resolution chooses the highest release the
    // source holds, and that version is pinned.
    private static async Task RecordTheVersionChosenByNameAsync(Rig rig, JobHistoryFixture fixture, string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(
            fixture, UploadState.Ready, cancellationToken, version: null, baseline: Rig.SeededVersion);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    // A job whose take refuses the version it named: the step fails the job with the take's
    // own code and message, nothing is pinned, and no later step of the run starts.
    private static async Task RecordTheVersionTheTakeRefusesAsync(Rig rig, JobHistoryFixture fixture, string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(
            fixture, UploadState.Ready, cancellationToken, version: Rig.RefusedByTheTakeVersion);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    // A job that named only its template while the source is unreachable: the resolution
    // answers from the versions already taken, and the step pins the version recorded when the
    // job was accepted — never the newer one a later job took.
    private static async Task RecordTheBaselineOfAnUnreachableSourceAsync(Rig rig, JobHistoryFixture fixture, string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(
            fixture, UploadState.Ready, cancellationToken, version: null, baseline: Rig.SeededVersion);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    // A job whose input is ready at its first check and whose data step commits: the run
    // moves the job to its first stage, partitions, and reaches its finalising step. The
    // job's PDF output has not started, so the step writes nothing — the recording is of
    // the path, not of a terminal status.
    private static async Task RecordTheFinalizedPathAsync(Rig rig, string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(JobHistoryFixtures.Finalized, UploadState.Ready, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(
            directory,
            JobHistoryFixtures.Finalized,
            await rig.HistoryAsync(reportJobId, runId, cancellationToken),
            cancellationToken);
    }

    // The data step's own refusal, recorded from a real execution: a snapshot the reader
    // refuses — a record cut short — ends the job with the profile's diagnostic, after one
    // attempt of the step and with no rendering step after it.
    private static async Task RecordTheRefusedSnapshotAsync(Rig rig, string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(
            JobHistoryFixtures.RefusedSnapshot, UploadState.Ready, cancellationToken, rig.RefusedDocument());
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(
            directory,
            JobHistoryFixtures.RefusedSnapshot,
            await rig.HistoryAsync(reportJobId, runId, cancellationToken),
            cancellationToken);
    }

    // A job whose template version carries a schema outside the profile: the pin is
    // sourced, the schema is refused, and the job ends with the code of a template that
    // cannot be used — again in one attempt, with nothing rendered.
    private static async Task RecordTheTemplateThatCannotBeUsedAsync(Rig rig, string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(
            JobHistoryFixtures.TemplateThatCannotBeUsed,
            UploadState.Ready,
            cancellationToken,
            rig.SampleDocument(),
            RefusedVersion);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(
            directory,
            JobHistoryFixtures.TemplateThatCannotBeUsed,
            await rig.HistoryAsync(reportJobId, runId, cancellationToken),
            cancellationToken);
    }

    private static string Target()
    {
        var target = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(target))
        {
            Assert.Skip($"the capture runs once, when {OutputVariable} names the directory to write.");
        }

        Directory.CreateDirectory(target!);
        return target!;
    }

    private static async Task RecordTheFirstCheckAsync(
        Rig rig,
        JobHistoryFixture fixture,
        UploadState state,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, state, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    private static async Task RecordTheTimerEndingTheWaitAsync(
        Rig rig,
        JobHistoryFixture fixture,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, UploadState.Verifying, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.WaitForItsWaitAsync(reportJobId, runId, fixture, cancellationToken);

        // The upload becomes usable only after the job has checked it once and gone to
        // sleep, so the check that follows is the one the job's own timer wakes. Nothing
        // notifies the job: this recording is the guarantee that no notification is needed.
        await rig.MakeReadyAsync(rig.UploadOf(reportJobId), cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    private static async Task RecordTheNotificationEndingTheWaitAsync(
        Rig rig,
        JobHistoryFixture fixture,
        UploadEnding ending,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, UploadState.Verifying, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.WaitForItsWaitAsync(reportJobId, runId, fixture, cancellationToken);

        switch (ending)
        {
            case UploadEnding.Ready:
                await rig.MakeReadyAsync(rig.UploadOf(reportJobId), cancellationToken);
                break;

            case UploadEnding.Rejected:
                await rig.MakeRejectedAsync(rig.UploadOf(reportJobId), cancellationToken);
                break;

            case UploadEnding.Expired:
                // Nothing marks the upload expired: the job's own check answers that its
                // verification deadline has passed, which is the rule the product applies.
                rig.AgePastTheVerificationDeadline();
                break;

            default:
                throw new InvalidOperationException("the capture does not know this ending.");
        }

        await rig.NotifyAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    private static async Task RecordTheRunThatContinuedAsync(
        Rig rig,
        JobHistoryFixture fixture,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, UploadState.Verifying, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);

        await rig.WaitForTheContinuationAsync(reportJobId, runId, fixture, cancellationToken);
        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);

        // The run that followed keeps waiting for an upload that never settles; it is not
        // part of this recording, so the capture ends the execution before the next one.
        await rig.Client.GetWorkflowHandle<ReportJobWorkflow>(Rig.WorkflowIdOf(reportJobId))
            .TerminateAsync("the run that continued was recorded and the run after it is not part of it");
    }

    private static async Task RecordTheRunAfterTheContinuationAsync(
        Rig rig,
        JobHistoryFixture fixture,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, UploadState.Verifying, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);

        var nextRunId = await rig.WaitForTheNextRunAsync(reportJobId, runId, fixture, cancellationToken);
        await rig.WaitForItsWaitAsync(reportJobId, nextRunId, fixture, cancellationToken);

        // The notification arrives while the run that followed the continuation is already
        // waiting, and the check it wakes is the one that ends the job with its upload.
        await rig.MakeReadyAsync(rig.UploadOf(reportJobId), cancellationToken);
        await rig.NotifyAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, nextRunId, cancellationToken), cancellationToken);
    }

    private static async Task WriteAsync(
        string directory,
        JobHistoryFixture fixture,
        WorkflowHistory history,
        CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(Path.Combine(directory, fixture.File), history.ToJson(), cancellationToken);

    /// <summary>
    /// One engine, two workers — one per queue — one clock, one storage root and the
    /// records every recording runs over. The engine allows one worker per queue, so the
    /// recordings share it and differ in the settings the workflow publishes before each
    /// execution starts; the data step is served from the queue C.0 schedules it on.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly WorkflowEnvironment _environment;
        private readonly TemporalWorker _worker;
        private readonly TemporalWorker _dataWorker;
        private readonly OrchestrationMetrics _metrics = new(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _running;
        private readonly Task _dataRunning;
        private readonly TemplateSourceClass? _source;
        private readonly SwitchableStore? _sourceStore;
        private readonly TemporalWorker? _templateWorker;
        private readonly Task? _templateRunning;
        private readonly Dictionary<string, string> _uploadOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (ObjectKey Key, ObjectWrite Written, long Size)> _snapshotOf = new(StringComparer.Ordinal);
        private int _sequence;

        private Rig(
            WorkflowEnvironment environment,
            TemporalWorker worker,
            TemporalWorker dataWorker,
            OrchestrationMetrics metrics,
            Task running,
            Task dataRunning,
            InMemoryUploadStore uploads,
            InMemoryJobStore jobs,
            InMemoryTemplateVersionStore versions,
            PreparedDataContractCache contracts,
            IObjectStore objects,
            string storageRoot,
            ManualTimeProvider clock,
            byte[] sample,
            byte[] refused,
            byte[] rowBound,
            TemplateSourceClass? source,
            SwitchableStore? sourceStore,
            TemporalWorker? templateWorker,
            Task? templateRunning)
        {
            _environment = environment;
            _worker = worker;
            _dataWorker = dataWorker;
            _metrics = metrics;
            _running = running;
            _dataRunning = dataRunning;
            Uploads = uploads;
            Jobs = jobs;
            Versions = versions;
            Contracts = contracts;
            Objects = objects;
            StorageRoot = storageRoot;
            Clock = clock;
            SampleDocumentBytes = sample;
            RefusedDocumentBytes = refused;
            RowBoundDocumentBytes = rowBound;
            _source = source;
            _sourceStore = sourceStore;
            _templateWorker = templateWorker;
            _templateRunning = templateRunning;
        }

        /// <summary>The version the rig already holds as taken when a recording starts.</summary>
        public const string SeededVersion = Version;

        /// <summary>The smallest version the source holds and the rig has not taken: the exact take reads it.</summary>
        public const string SmallestUntakenVersion = "1.2.0";

        /// <summary>The version whose package the take refuses: its manifest declares another version.</summary>
        public const string RefusedByTheTakeVersion = "1.3.0";

        /// <summary>The highest release the source holds: the by-name resolution chooses it.</summary>
        public const string HighestReleaseVersion = "1.4.0";

        public ITemporalClient Client => _environment.Client;

        public InMemoryUploadStore Uploads { get; }

        public InMemoryJobStore Jobs { get; }

        /// <summary>The taken versions the recorded jobs are pinned to.</summary>
        public InMemoryTemplateVersionStore Versions { get; }

        /// <summary>The contracts this rig prepared while the recordings ran.</summary>
        public PreparedDataContractCache Contracts { get; }

        /// <summary>The storage root the snapshots and the taken packages are written to.</summary>
        public IObjectStore Objects { get; }

        /// <summary>The root of this rig's storage, removed when the rig ends.</summary>
        public string StorageRoot { get; }

        /// <summary>The pilot's own sample document, which every re-taken job's snapshot holds.</summary>
        private byte[] SampleDocumentBytes { get; }

        /// <summary>The document whose second record is cut short: the reader refuses it.</summary>
        private byte[] RefusedDocumentBytes { get; }

        /// <summary>The document whose second record is over the row bound: the pass refuses it.</summary>
        private byte[] RowBoundDocumentBytes { get; }

        /// <summary>
        /// The instant the recording being taken writes its records with. It never follows
        /// the wall clock, and the one recording that moves it forward seeds the records it
        /// needs from where it has got to, so no recording inherits another one's instant.
        /// </summary>
        public ManualTimeProvider Clock { get; }

        public static string WorkflowIdOf(string reportJobId) => ReportJobWorkflow.WorkflowIdFor(reportJobId);

        /// <summary>The pilot's own sample document, copied for a job's snapshot.</summary>
        public byte[] SampleDocument() => [.. SampleDocumentBytes];

        /// <summary>The document the reader refuses: two records, the second cut short.</summary>
        public byte[] RefusedDocument() => [.. RefusedDocumentBytes];

        /// <summary>
        /// The document the row bound refuses: the sample's header and two of its records,
        /// the second one's description written with 262 145 ASCII bytes.
        /// </summary>
        public byte[] RowBoundDocument() => [.. RowBoundDocumentBytes];

        public static async Task<Rig> StartAsync(bool withTemplateStep = false, bool withPdfOutput = false)
        {
            var uploads = new InMemoryUploadStore();
            var jobs = new InMemoryJobStore(uploads);
            var versions = new InMemoryTemplateVersionStore();
            var contracts = new PreparedDataContractCache();
            var clock = new ManualTimeProvider(Now);
            var metrics = new OrchestrationMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName));

            // The storage the data step reads: the snapshots of the recorded jobs and the
            // packages of the versions they are pinned to, under a root of this rig's own.
            var storageRoot = Path.Combine(
                Path.GetTempPath(),
                "report-service-capture",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(storageRoot);
            var objects = FilesystemObjectStore.Open(
                new FilesystemStorageOptions { Root = storageRoot, CreateIfMissing = true },
                storageRoot,
                TimeProvider.System,
                NullLogger<FilesystemObjectStore>.Instance);

            var sample = await ReadRepositoryFileAsync("samples/templates/pilot-grouped/sample.json");
            var refused = RefusedDocumentOf(sample);
            var rowBound = RowBoundDocumentOf(sample);
            await SeedVersionAsync(objects, versions, Version, await ReadRepositoryFileAsync("samples/templates/pilot-grouped/schema.json"), Consumer);
            await SeedVersionAsync(objects, versions, RefusedVersion, RefusedSchema(), Consumer);

            var activity = new ValidateAndPartitionActivity(
                jobs,
                uploads,
                objects,
                versions,
                contracts,
                clock,
                NullLogger<ValidateAndPartitionActivity>.Instance,
                Path.Combine(storageRoot, "scratch"));

            // The workflow's own bounds are published per recording, before each execution
            // starts; the worker serves every recording of this rig in turn.
            var environment = await WorkflowEnvironment.StartLocalAsync(new WorkflowEnvironmentStartLocalOptions());
            var workerOptions = new TemporalWorkerOptions(ReportJobWorkflow.OrchestrationQueue)
                .AddWorkflow<ReportJobWorkflow>()
                .AddAllActivities(new ReportJobActivities(
                    jobs,
                    clock,
                    metrics,
                    NullLoggerFactory.Instance,
                    NullLogger<ReportJobActivities>.Instance))
                .AddAllActivities(new JobTransitionActivities(
                    jobs,
                    clock,
                    metrics,
                    NullLoggerFactory.Instance,
                    NullLogger<JobTransitionActivities>.Instance))
                .AddAllActivities(new InputWaitActivities(jobs, NullLoggerFactory.Instance, NullLogger<InputWaitActivities>.Instance));

            // The output lookup is registered only for the recording of the refusal, so every
            // other recording keeps the commands it was captured with: the recording of that
            // path is the one whose run looks its output up and ends it.
            if (withPdfOutput)
            {
                workerOptions.AddAllActivities(new PdfOutputActivities(
                    jobs,
                    clock,
                    Options.Create(new RetentionOptions { Outputs = TimeSpan.FromDays(7) }),
                    NullLoggerFactory.Instance,
                    NullLogger<PdfOutputActivities>.Instance));
            }

            // The template step and the two activities it calls on the PDF worker's queue: they
            // are registered only for the recordings of the step, so the earlier recordings keep
            // replaying the runs they were captured from.
            TemplateSourceClass? source = null;
            SwitchableStore? sourceStore = null;
            TemporalWorker? templateWorker = null;
            Task? templateRunning = null;
            if (withTemplateStep)
            {
                // The versions the step takes carry the pilot's own schema, so the data step
                // that follows reads the same contract the bound version's record does.
                var pilotSchema = await ReadRepositoryFileAsync("samples/templates/pilot-grouped/schema.json");
                Dictionary<string, byte[]> PackageWithThePilotSchema(string version)
                {
                    var package = TemplatePackageFixtures.Valid(TemplateId, version, owner: Consumer, shared: true);
                    package["schema.json"] = pilotSchema;
                    return package;
                }

                sourceStore = new SwitchableStore(objects);
                source = TemplateSourceClass.Over(sourceStore);
                foreach (var version in new[] { SmallestUntakenVersion, HighestReleaseVersion })
                {
                    await source.PublishAsync(TemplateId, version, PackageWithThePilotSchema(version));
                }

                await source.PublishAsync(
                    TemplateId,
                    RefusedByTheTakeVersion,
                    TemplatePackageFixtures.FolderDoesNotMatchTheVersion(TemplateId, RefusedByTheTakeVersion, RefusedByTheTakeVersion + ".1"));

                var sourceOptions = new TemplateSourceOptions();
                var reader = new StorageTemplateSource(
                    sourceStore,
                    sourceOptions,
                    new TemplateSourceMetrics(),
                    NullLogger<StorageTemplateSource>.Instance);
                var take = new TemplateTake(
                    reader,
                    versions,
                    objects,
                    new TemplateManifestReader(),
                    new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance),
                    [],
                    new TemplateTakeMetrics(),
                    clock,
                    Options.Create(sourceOptions),
                    NullLogger<TemplateTake>.Instance);
                var resolver = new TemplateVersionResolver(
                    reader,
                    versions,
                    new TemplateTakeAdapter(take),
                    new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance),
                    new TemplateByNameMetrics(),
                    clock,
                    NullLogger<TemplateVersionResolver>.Instance,
                    sourceOptions.ResolveDeadline);

                workerOptions.AddAllActivities(new TemplateResolutionActivities(
                    jobs,
                    versions,
                    clock,
                    metrics,
                    NullLoggerFactory.Instance,
                    NullLogger<TemplateResolutionActivities>.Instance));
                templateWorker = new TemporalWorker(
                    environment.Client,
                    new TemporalWorkerOptions(TemplateResolutionActivityOptions.Queue)
                        .AddAllActivities(new TakeTemplateVersionActivity(take))
                        .AddAllActivities(new ResolveTemplateVersionActivity(resolver)));
            }

            var worker = new TemporalWorker(environment.Client, workerOptions);
            var dataWorker = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(PdfOutputActivityOptions.DataQueue)
                    .AddAllActivities(activity));

            var stop = new CancellationTokenSource();
            var running = worker.ExecuteAsync(stop.Token);
            var dataRunning = dataWorker.ExecuteAsync(stop.Token);
            templateRunning = templateWorker?.ExecuteAsync(stop.Token);
            return new Rig(
                environment,
                worker,
                dataWorker,
                metrics,
                running,
                dataRunning,
                uploads,
                jobs,
                versions,
                contracts,
                objects,
                storageRoot,
                clock,
                sample,
                refused,
                rowBound,
                source,
                sourceStore,
                templateWorker,
                templateRunning);
        }

        /// <summary>Stops answering the template source: the next resolution finds it unreachable.</summary>
        public void StopTheSource() => _sourceStore!.Fail = true;

        public string UploadOf(string reportJobId) => _uploadOf[reportJobId];

        public WorkflowHandle<ReportJobWorkflow> Handle(string reportJobId, string runId) =>
            Client.GetWorkflowHandle<ReportJobWorkflow>(WorkflowIdOf(reportJobId), runId: runId);

        public async Task<string> GivenJobAsync(
            JobHistoryFixture fixture,
            UploadState state,
            CancellationToken cancellationToken,
            byte[]? document = null,
            string? version = Version,
            string? baseline = null)
        {
            ReportJobWorkflow.Configure(fixture.Backstop, fixture.ContinueAsNewAfter);
            var now = Clock.GetUtcNow();
            document ??= SampleDocument();

            var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", CultureInfo.InvariantCulture);
            var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");

            // The document is written first, and the record that describes it carries that
            // document's own size and SHA-256 in both pairs: a record never names bytes that
            // were not written.
            var snapshotKey = ObjectKey.FromSegments("snapshots", uploadId, "a");
            using var content = new MemoryStream(document, writable: false);
            var written = await Objects.WriteAsync(snapshotKey, content, cancellationToken);
            if (!written.Succeeded)
            {
                throw new InvalidOperationException("the capture's snapshot had to be written");
            }

            await Uploads.CreateAsync(
                new UploadRecord(
                    uploadId,
                    Consumer,
                    UploadMode.ApiStream,
                    DataProfile.HeaderItemsV1,
                    UploadStatus.Created,
                    stagingKey,
                    now.AddHours(-1),
                    now.AddHours(1)),
                cancellationToken);
            await Uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now.AddMinutes(-30), cancellationToken);
            await Uploads.RecordTransferAsync(
                uploadId,
                UploadContentOutcome.Complete,
                written.Value!.Size,
                written.Value.Size,
                written.Value.Sha256,
                now.AddMinutes(-30),
                cancellationToken);
            await Uploads.ConfirmAsync(
                uploadId, Consumer, null, now.AddMinutes(-30), now.AddMinutes(-30), now.AddHours(5), cancellationToken);

            if (state is UploadState.Ready)
            {
                await CommitSnapshotAsync(uploadId, snapshotKey, written.Value, document.LongLength, cancellationToken);
            }

            _snapshotOf[uploadId] = (snapshotKey, written.Value, document.LongLength);

            var acceptance = new JobAcceptance(
                JobIdentifier.Create(),
                JobIdentifier.CreateOutput(),
                Consumer,
                uploadId,
                TemplateId,
                version is null ? TemplateReferenceForm.ByName : TemplateReferenceForm.Exact,
                version) with
            {
                // A by-name job records the version it may fall back to when the source cannot
                // be checked; an exact one has no baseline.
                BaselineVersion = baseline,
            };
            if (await Jobs.AcceptAsync(acceptance, now, cancellationToken) is not JobAcceptanceResult.Accepted)
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"the capture's job over {uploadId} had to be accepted"));
            }

            if (state is UploadState.Rejected)
            {
                await MakeRejectedAsync(uploadId, cancellationToken);
            }

            _uploadOf[acceptance.ReportJobId] = uploadId;
            return acceptance.ReportJobId;
        }

        public async Task<(WorkflowHandle<ReportJobWorkflow> Handle, string RunId)> StartJobAsync(
            string reportJobId,
            CancellationToken cancellationToken)
        {
            var handle = await Client.StartWorkflowAsync(
                (ReportJobWorkflow workflow) => workflow.RunAsync(new ReportJobInput(reportJobId)),
                new WorkflowOptions
                {
                    Id = WorkflowIdOf(reportJobId),
                    TaskQueue = ReportJobWorkflow.OrchestrationQueue,
                });

            // The run this execution started is read back from the engine rather than from
            // the handle: a handle addresses a workflow, and only the engine says which of
            // its runs is current once a run continues as a new one.
            var described = await Client.WorkflowService.DescribeWorkflowExecutionAsync(
                Describe(handle.Id),
                new RpcOptions { CancellationToken = cancellationToken });
            return (handle, described.WorkflowExecutionInfo.Execution.RunId);
        }

        public async Task AwaitTheEndAsync(
            WorkflowHandle<ReportJobWorkflow> handle,
            string reportJobId,
            CancellationToken cancellationToken)
        {
            try
            {
                await handle.GetResultAsync().WaitAsync(ExecutionDeadline, cancellationToken);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                throw await DescribeTheUnfinishedRunAsync(handle, reportJobId, exception, cancellationToken);
            }
        }

        public async Task WaitForItsWaitAsync(
            string reportJobId,
            string runId,
            JobHistoryFixture fixture,
            CancellationToken cancellationToken) =>
            await Wait.UntilAsync(
                $"{fixture.File}: the run to check the upload once and wait for it",
                async token => EventsOf(await FetchAsync(Handle(reportJobId, runId), token))
                    .Any(entry => entry.EventType == EventType.TimerStarted),
                ExecutionDeadline,
                TimeSpan.FromMilliseconds(50),
                cancellationToken);

        public async Task WaitForTheContinuationAsync(
            string reportJobId,
            string runId,
            JobHistoryFixture fixture,
            CancellationToken cancellationToken) =>
            await Wait.UntilAsync(
                $"{fixture.File}: the run to take every check it may and continue as a new one",
                async token => EventsOf(await FetchAsync(Handle(reportJobId, runId), token))
                    .Any(entry => entry.EventType == EventType.WorkflowExecutionContinuedAsNew),
                ExecutionDeadline,
                TimeSpan.FromMilliseconds(50),
                cancellationToken);

        public async Task<string> WaitForTheNextRunAsync(
            string reportJobId,
            string runId,
            JobHistoryFixture fixture,
            CancellationToken cancellationToken)
        {
            string? next = null;
            await Wait.UntilAsync(
                $"{fixture.File}: the run to continue as a new one",
                async token =>
                {
                    var described = await Client.WorkflowService.DescribeWorkflowExecutionAsync(
                        Describe(WorkflowIdOf(reportJobId)),
                        new RpcOptions { CancellationToken = token });
                    next = described.WorkflowExecutionInfo.Execution.RunId;
                    return !string.Equals(next, runId, StringComparison.Ordinal);
                },
                ExecutionDeadline,
                TimeSpan.FromMilliseconds(50),
                cancellationToken);
            return next!;
        }

        public Task<WorkflowHistory> HistoryAsync(
            string reportJobId,
            string runId,
            CancellationToken cancellationToken) => FetchAsync(Handle(reportJobId, runId), cancellationToken);

        public Task NotifyAsync(string reportJobId, CancellationToken cancellationToken) =>
            Client.GetWorkflowHandle<ReportJobWorkflow>(WorkflowIdOf(reportJobId))
                .SignalAsync((ReportJobWorkflow workflow) => workflow.OnInputChangedAsync());

        // The snapshot record is committed from the document that was written: its own byte
        // count and SHA-256 in both pairs, because nothing compressed it on the way in.
        public Task MakeReadyAsync(string uploadId, CancellationToken cancellationToken)
        {
            var (key, written, size) = _snapshotOf[uploadId];
            return CommitSnapshotAsync(uploadId, key, written, size, cancellationToken);
        }

        private async Task CommitSnapshotAsync(
            string uploadId,
            ObjectKey snapshotKey,
            ObjectWrite written,
            long sizeUncompressed,
            CancellationToken cancellationToken) =>
            await Uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    snapshotKey,
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    written.Sha256,
                    sizeUncompressed,
                    written.Sha256,
                    written.Size,
                    Clock.GetUtcNow().AddMinutes(-20)),
                Clock.GetUtcNow().AddMinutes(-20),
                cancellationToken);

        public async Task MakeRejectedAsync(string uploadId, CancellationToken cancellationToken) =>
            await Uploads.CommitRejectedAsync(
                uploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, Clock.GetUtcNow(), cancellationToken);

        /// <summary>Moves the clock past the verification deadline of the upload being recorded.</summary>
        public void AgePastTheVerificationDeadline() => Clock.Advance(TimeSpan.FromHours(6));

        // A file of the repository, read from its real place: the pilot's own sample and
        // the schema of the version the recorded jobs are pinned to.
        private static Task<byte[]> ReadRepositoryFileAsync(string relativePath) =>
            File.ReadAllBytesAsync(Path.Combine(
                RepositoryPaths.Root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

        // The pilot's own sample, cut inside its second record: the reader reports the cut
        // where it stopped and never the values it had read.
        private static byte[] RefusedDocumentOf(byte[] sample)
        {
            var text = Encoding.UTF8.GetString(sample);
            var first = text.IndexOf("\"sequence\"", StringComparison.Ordinal);
            var second = text.IndexOf("\"sequence\"", first + 1, StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(text[..(second + 48)]);
        }

        // The pilot's own sample cut to two records, the second one's description written with
        // 262 145 ASCII bytes: the schema declares that field with no length limit, so the
        // document is valid data whose last record the row bound refuses. Two records keep the
        // block far under its own byte maximum, so the row bound is the first rule to fire.
        private static byte[] RowBoundDocumentOf(byte[] sample)
        {
            var document = JsonNode.Parse(sample)!;
            var items = document["items"]!.AsArray();
            var kept = new JsonArray();
            for (var index = 0; index < 2; index++)
            {
                kept.Add(items[index]!.DeepClone());
            }

            kept[1]!["description"] = new string('A', 262_145);
            document["items"] = kept;
            document["header"]!["totals"]!["recordCount"] = 2;
            return Encoding.UTF8.GetBytes(document.ToJsonString());
        }

        // A schema outside the profile: an identifier of its own, which the profile refuses
        // before the library ever sees it. The version is taken, the pin is sourced, and the
        // job ends because its template cannot be used.
        private static byte[] RefusedSchema() =>
            Encoding.UTF8.GetBytes(
                """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "$id": "https://templates.invalid/data/refused",
                  "properties": {
                    "header": { "type": "object" },
                    "items": { "items": { "type": "object" } }
                  }
                }
                """);

        // The taken version the recorded jobs are pinned to: its package written through the
        // archive the take uses, stored under its own hash, with its schema at the path the
        // record names, and the record of that version in the store the step reads.
        private static async Task SeedVersionAsync(
            IObjectStore objects,
            InMemoryTemplateVersionStore versions,
            string version,
            byte[] schema,
            string owner)
        {
            var files = new List<PackageFileSource>
            {
                new(SchemaPath, _ => Task.FromResult<Stream>(new MemoryStream(schema, writable: false))),
                new("template.yaml", _ => Task.FromResult<Stream>(new MemoryStream(
                    Encoding.UTF8.GetBytes("engine: scriban\n"), writable: false))),
            };

            using var sink = new MemoryStream();
            await TemplatePackageArchive.WriteAsync(files, sink, CancellationToken.None);
            var bytes = sink.ToArray();
            var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var key = TemplatePackageKeys.ForVersion(owner, TemplateId, version, packageSha256);
            using var content = new MemoryStream(bytes, writable: false);
            var written = await objects.WriteAsync(key, content, CancellationToken.None);
            if (!written.Succeeded)
            {
                throw new InvalidOperationException("the capture's package had to be written");
            }

            versions.Seed(TemplateVersionRecords.Sample(
                TemplateId,
                version,
                owner,
                shared: true,
                schemaRef: SchemaPath,
                packageSha256: packageSha256) with
            {
                PackageSize = bytes.Length,
                FileCount = files.Count,
                ObjectKey = key,
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();

            // A worker is stopped by being disposed. This version's run task does not report
            // that it has finished, so it is given a moment and then left to the process
            // rather than holding the capture open for a task that never completes.
            _worker.Dispose();
            _dataWorker.Dispose();
            _templateWorker?.Dispose();
            try
            {
                await Task.WhenAll(_running, _dataRunning).WaitAsync(TimeSpan.FromSeconds(5));
                if (_templateRunning is not null)
                {
                    await _templateRunning.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // The workers are stopped; only their tasks' report is missing.
            }

            _stop.Dispose();
            await _environment.DisposeAsync();
            _metrics.Dispose();
            Contracts.Dispose();
            if (_source is not null)
            {
                await _source.DisposeAsync();
            }

            try
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
            catch (IOException)
            {
                // A storage root left on disk costs disk, never correctness.
            }

            await Task.CompletedTask;
        }

        /// <summary>A store whose source read can be made to fail, as an outage does, and answer again.</summary>
    public sealed class SwitchableStore(IObjectStore inner) : IObjectStore
    {
        /// <summary>Whether the source class answers an outage.</summary>
        public bool Fail { get; set; }

        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            inner.WriteAsync(key, content, cancellationToken);

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) =>
            Fail
                ? Task.FromResult(StorageResult.Failure<VerifiedRead>(StorageOutcome.Transient, Failure()))
                : inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Fail
                ? Task.FromResult(StorageResult.Failure<ObjectInfo>(StorageOutcome.Transient, Failure()))
                : inner.GetInfoAsync(key, cancellationToken);

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, cancellationToken);

        public async IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new StorageException(StorageOutcome.Transient, Failure());
            }

            await foreach (var item in inner.ListAsync(classPrefix, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }

        private static StorageError Failure() => new("storage.transient", "The object store did not answer.");
    }
    private static DescribeWorkflowExecutionRequest Describe(string workflowId) => new()        {
            Namespace = "default",
            Execution = new Temporalio.Api.Common.V1.WorkflowExecution
            {
                WorkflowId = workflowId,
            },
        };

        private async Task<WorkflowHistory> FetchAsync(
            WorkflowHandle<ReportJobWorkflow> handle,
            CancellationToken cancellationToken) =>
            await handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions()).WaitAsync(ExecutionDeadline, cancellationToken);

        private async Task<InvalidOperationException> DescribeTheUnfinishedRunAsync(
            WorkflowHandle<ReportJobWorkflow> handle,
            string reportJobId,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var failed = await FetchAsync(handle, cancellationToken);
            var described = await Client.WorkflowService.DescribeWorkflowExecutionAsync(
                Describe(handle.Id),
                new RpcOptions { CancellationToken = cancellationToken });
            var pending = string.Join(
                " | ",
                described.PendingActivities.Select(activity => $"{activity.ActivityType.Name} attempt {activity.Attempt}: {activity.LastFailure?.Message}"));
            return new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the recorded job {reportJobId} did not finish: {string.Join(", ", EventsOf(failed).Select(entry => entry.EventType.ToString()))}; pending: {pending}; {exception.Message}"),
                exception);
        }

        private static IEnumerable<Temporalio.Api.History.V1.HistoryEvent> EventsOf(WorkflowHistory history) => history.Events;
    }
}

