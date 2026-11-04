extern alias orchestration;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.DataContract;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;
using InputWaitActivities = orchestration::ReportService.Workers.Orchestration.Jobs.InputWaitActivities;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;
using ReportJobActivities = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobActivities;
using ReportJobInput = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobInput;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;
using ValidateAndPartitionActivity = orchestration::ReportService.Workers.Orchestration.Jobs.ValidateAndPartitionActivity;
using PdfOutputActivityOptions = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivityOptions;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// Records the job histories the hermetic suite replays, once, so that suite has something
/// to check. It is opt-in: it runs only when the environment variable names the directory
/// to write, so the integration command never runs it, and the ordinary cases of this suite
/// are untouched by it. It needs one engine and nothing else — the jobs it runs live in the
/// in-memory record stores and a storage root of its own — so every history it writes is the
/// outcome of a real execution of the workflow and its steps, taken under the very settings
/// the case that replays it replays it under. The data step runs for real: the records name
/// a taken version whose package and schema are written behind them, and every snapshot
/// document is written before the record that carries its size and SHA-256.
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
    /// Records every history the hermetic suite replays: a job whose first check of its
    /// upload already answered, a job whose own timer ends its wait, the two ways a
    /// notification ends it, the upload that outlived its verification deadline, and the
    /// two runs of a continuation.
    /// </summary>
    [Fact]
    public async Task EveryReplayedJobHistoryIsRecorded()
    {
        var target = Target();
        await using var rig = await Rig.StartAsync();

        await RecordTheFirstCheckAsync(rig, JobHistoryFixtures.Ready, UploadState.Ready, target);
        await RecordTheFirstCheckAsync(rig, JobHistoryFixtures.Rejected, UploadState.Rejected, target);

        await RecordTheTimerEndingTheWaitAsync(rig, JobHistoryFixtures.Verifying, target);
        await RecordTheNotificationEndingTheWaitAsync(
            rig, JobHistoryFixtures.WaitingThenReadyAfterANotification, UploadEnding.Ready, target);
        await RecordTheNotificationEndingTheWaitAsync(
            rig, JobHistoryFixtures.WaitingThenRejected, UploadEnding.Rejected, target);
        await RecordTheNotificationEndingTheWaitAsync(
            rig, JobHistoryFixtures.WaitingThenExpired, UploadEnding.Expired, target);

        await RecordTheRunThatContinuedAsync(rig, JobHistoryFixtures.ContinuedAsNew, target);
        await RecordTheRunAfterTheContinuationAsync(rig, JobHistoryFixtures.ContinuedAfterANotification, target);

        await RecordTheRefusedSnapshotAsync(rig, target);
        await RecordTheTemplateThatCannotBeUsedAsync(rig, target);
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
            byte[] refused)
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
        }

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

        public static async Task<Rig> StartAsync()
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
            var worker = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(ReportJobWorkflow.OrchestrationQueue)
                    .AddWorkflow<ReportJobWorkflow>()
                    .AddAllActivities(new ReportJobActivities(
                        jobs,
                        clock,
                        metrics,
                        NullLoggerFactory.Instance,
                        NullLogger<ReportJobActivities>.Instance))
                    .AddAllActivities(new InputWaitActivities(jobs, NullLoggerFactory.Instance, NullLogger<InputWaitActivities>.Instance)));
            var dataWorker = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(PdfOutputActivityOptions.DataQueue)
                    .AddAllActivities(activity));

            var stop = new CancellationTokenSource();
            var running = worker.ExecuteAsync(stop.Token);
            var dataRunning = dataWorker.ExecuteAsync(stop.Token);
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
                refused);
        }

        public string UploadOf(string reportJobId) => _uploadOf[reportJobId];

        public WorkflowHandle<ReportJobWorkflow> Handle(string reportJobId, string runId) =>
            Client.GetWorkflowHandle<ReportJobWorkflow>(WorkflowIdOf(reportJobId), runId: runId);

        public async Task<string> GivenJobAsync(
            JobHistoryFixture fixture,
            UploadState state,
            CancellationToken cancellationToken,
            byte[]? document = null,
            string version = Version)
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
                TemplateReferenceForm.Exact,
                version);
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
            try
            {
                await Task.WhenAll(_running, _dataRunning).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // The workers are stopped; only their tasks' report is missing.
            }

            _stop.Dispose();
            await _environment.DisposeAsync();
            _metrics.Dispose();
            Contracts.Dispose();
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
