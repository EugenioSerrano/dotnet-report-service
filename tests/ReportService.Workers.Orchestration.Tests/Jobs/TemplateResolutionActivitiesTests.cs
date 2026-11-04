using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The three metadata steps that fix a job's template, exercised in the engine's own host
/// for one attempt: a version already taken and usable is bound with one read and no
/// source, one this consumer may not use is refused exactly like an absent one, a version
/// that is not taken asks the workflow for the take, a by-name reference asks for the
/// resolution, and the baseline recorded at acceptance is what an unreachable source falls
/// back to - never a version taken later. The pin is written once: a second attempt, a
/// newer version and a job that ended all leave the record where the first write put it.
/// </summary>
public sealed class TemplateResolutionActivitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private const string Template = "invoice-energy";

    [Fact]
    public async Task ATakenVersionThisConsumerMayUseIsBoundWithoutASourceRead()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.1.0");

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.Pinned, result.Outcome);
        Assert.Equal(TemplatePinPaths.Taken, result.Path);
        Assert.Equal(1, rig.Versions.FindCalls);
        Assert.Equal("1.1.0", rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin!.ResolvedVersion);
    }

    [Fact]
    public async Task ATakenVersionAnotherConsumerOwnsAndDoesNotShareIsRefusedAsNotFound()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.1.0", owner: "another-consumer");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PrepareAsync());

        Assert.Equal("TEMPLATE_NOT_FOUND", failure.ErrorType);
        Assert.Equal(TemplateFailureCodes.NotFoundMessage, failure.Message);
        Assert.True(failure.NonRetryable);
        Assert.Contains(rig.Logs.Lines, line => line.Contains("template-not-found", StringComparison.Ordinal));
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task AnUntakenExactVersionAsksForTheTakeWithTheJobsConsumer()
    {
        using var rig = await Rig.CreateAsync("1.0.0");
        rig.Take("1.1.0");

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.NeedsTake, result.Outcome);
        Assert.Equal(Template, result.TemplateId);
        Assert.Equal("1.0.0", result.RequestedVersion);
        Assert.Equal(Rig.Consumer, result.RequestingConsumerId);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task AReferenceByNameAsksForTheResolutionWithTheJobsConsumer()
    {
        using var rig = await Rig.CreateAsync(null);

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.NeedsByName, result.Outcome);
        Assert.Equal(Template, result.TemplateId);
        Assert.Equal(Rig.Consumer, result.RequestingConsumerId);
    }

    [Fact]
    public async Task AJobThatAlreadyEndedIsTerminal()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        await rig.FailJobAsync();

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.Terminal, result.Outcome);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task APinWrittenByAnEarlierAttemptIsTheOneTheJobKeeps()
    {
        using var rig = await Rig.CreateAsync(null);
        rig.Take("1.1.0");
        await rig.PinAsync("1.1.0", taken: false);
        rig.Take("1.2.0");

        var second = await rig.PinAsync("1.2.0", taken: false);

        Assert.Equal(PrepareTemplateOutcome.Pinned, second.Outcome);
        Assert.Equal("1.1.0", rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin!.ResolvedVersion);
    }

    [Fact]
    public async Task AnExactReferenceIsNeverPinnedToAnotherVersion()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.2.0");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinAsync("1.2.0", taken: false));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task APinPresentOnTheRecordIsAnsweredWithoutReadingAnyVersion()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.1.0");
        await rig.PrepareAsync();
        var reads = rig.Versions.FindCalls;

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.Pinned, result.Outcome);
        Assert.Equal(TemplatePinPaths.Taken, result.Path);
        Assert.Equal(reads, rig.Versions.FindCalls);
    }

    [Fact]
    public async Task AWrittenPinOnAJobThatEndedMeanwhileIsTerminal()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        await rig.FailJobAsync();

        var result = await rig.PinAsync("1.1.0", taken: true);

        Assert.Equal(PrepareTemplateOutcome.Terminal, result.Outcome);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task APinOfAWorkersVersionForAJobOfAnotherConsumerIsRefused()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.2.0", owner: "another-consumer");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinAsync("1.2.0", taken: false));

        Assert.Equal("TEMPLATE_NOT_FOUND", failure.ErrorType);
    }

    [Fact]
    public async Task APinOfAVersionNoRecordAnswersIsAnExecutionFailure()
    {
        using var rig = await Rig.CreateAsync("1.1.0");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinAsync("7.7.7", taken: false));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.Equal(TemplateFailureCodes.RecordMissingMessage, failure.Message);
    }

    [Fact]
    public async Task AnUnreachableSourcePinsTheBaselineRecordedAtAcceptance()
    {
        using var rig = await Rig.CreateAsync(null);
        rig.SetBaseline("1.0.0");
        rig.Versions.Seed(TemplateVersionRecords.Sample(templateId: Template, version: "1.0.0", owner: Rig.Consumer));
        rig.Versions.Seed(TemplateVersionRecords.Sample(templateId: Template, version: "1.10.0", owner: Rig.Consumer));

        var result = await rig.PinBaselineAsync();

        Assert.Equal(PrepareTemplateOutcome.Pinned, result.Outcome);
        Assert.Equal(TemplatePinPaths.Baseline, result.Path);
        Assert.Equal("1.0.0", rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin!.ResolvedVersion);
        Assert.Contains(rig.Logs.Lines, line => line.Contains("taken when the job was accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreachableSourceWithoutABaselineFailsTheStepWithItsCode()
    {
        using var rig = await Rig.CreateAsync(null);
        rig.SetBaseline(null);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinBaselineAsync());

        Assert.Equal("TEMPLATE_SOURCE_UNAVAILABLE", failure.ErrorType);
        Assert.Equal(TemplateFailureCodes.SourceUnavailableMessage, failure.Message);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task ABaselineWhoseRecordIsGoneIsAnExecutionFailureAndNeverAnotherVersion()
    {
        using var rig = await Rig.CreateAsync(null);
        rig.SetBaseline("4.0.0");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinBaselineAsync());

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    /// <summary>
    /// One job over one upload, the record stores the steps read and write, the clock the
    /// test moves, and the engine's host around the activity one case runs.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        /// <summary>The consumer every case accepts its job for.</summary>
        public const string Consumer = "dev-consumer";

        private readonly OrchestrationMetrics _metrics = new(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        private readonly LogCapture _logs = new();
        private readonly ILoggerFactory _loggerFactory;

        private Rig()
        {
            Clock = new ManualTimeProvider(Now);
            Uploads = new InMemoryUploadStore();
            Jobs = new InMemoryJobStore(Uploads);
            Versions = new InMemoryTemplateVersionStore();
            _loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(_logs));
            Activities = new TemplateResolutionActivities(
                Jobs,
                Versions,
                Clock,
                _metrics,
                _loggerFactory,
                _loggerFactory.CreateLogger<TemplateResolutionActivities>());
        }

        public ManualTimeProvider Clock { get; }

        public InMemoryUploadStore Uploads { get; }

        public InMemoryJobStore Jobs { get; }

        public InMemoryTemplateVersionStore Versions { get; }

        public LogCapture Logs => _logs;

        public TemplateResolutionActivities Activities { get; }

        public string ReportJobId { get; private set; } = string.Empty;

        public static async Task<Rig> CreateAsync(string? version)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var rig = new Rig();
            var uploadId = "upload-0123456789abcdef0123456789abcdef";
            await rig.Uploads.CreateAsync(
                new UploadRecord(
                    uploadId,
                    Consumer,
                    UploadMode.ApiStream,
                    DataProfile.HeaderItemsV1,
                    UploadStatus.Created,
                    ObjectKey.FromSegments("staging", uploadId, "content"),
                    Now.AddHours(-1),
                    Now.AddHours(1)),
                cancellationToken);
            await rig.Uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken);
            await rig.Uploads.RecordTransferAsync(
                uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), Now.AddMinutes(-30), cancellationToken);
            await rig.Uploads.ConfirmAsync(
                uploadId, Consumer, null, Now.AddMinutes(-30), Now.AddMinutes(-30), Now.AddHours(5), cancellationToken);
            await rig.Uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    ObjectKey.FromSegments("snapshots", uploadId, "a"),
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    new string('a', 64),
                    900,
                    new string('a', 64),
                    900,
                    Now.AddMinutes(-20)),
                Now.AddMinutes(-20),
                cancellationToken);

            var acceptance = new JobAcceptance(
                JobIdentifier.Create(),
                JobIdentifier.CreateOutput(),
                Consumer,
                uploadId,
                Template,
                version is null ? TemplateReferenceForm.ByName : TemplateReferenceForm.Exact,
                version);
            Assert.IsType<JobAcceptanceResult.Accepted>(await rig.Jobs.AcceptAsync(acceptance, Now, cancellationToken));
            rig.ReportJobId = acceptance.ReportJobId;

            return rig;
        }

        /// <summary>Records a taken version, as the take would have left it before the job ran.</summary>
        /// <param name="version">The version that was taken.</param>
        /// <param name="owner">The consumer it belongs to.</param>
        /// <param name="shared">Whether every consumer may use it.</param>
        public void Take(string version, string owner = Consumer, bool shared = false) =>
            Versions.Seed(TemplateVersionRecords.Sample(templateId: Template, version: version, owner: owner, shared: shared));

        public Task<PrepareTemplateResult> PrepareAsync() =>
            new ActivityEnvironment().RunAsync(() => Activities.PrepareTemplateAsync(new PrepareTemplateInput(ReportJobId)));

        public Task<TemplatePinResult> PinAsync(string version, bool taken)
        {
            if (taken)
            {
                Versions.Seed(TemplateVersionRecords.Sample(templateId: Template, version: version, owner: Consumer));
            }

            return new ActivityEnvironment().RunAsync(() => Activities.PinTemplateAsync(new PinTemplateInput(ReportJobId, Template, version)));
        }

        public Task<TemplatePinResult> PinBaselineAsync() =>
            new ActivityEnvironment().RunAsync(() => Activities.PinBaselineAsync(new PinBaselineInput(ReportJobId)));

        public void Dispose()
        {
            _metrics.Dispose();
            _loggerFactory.Dispose();
            _logs.Dispose();
        }

        public async Task FailJobAsync() =>
            await Jobs.FailJobAsync(
                ReportJobId,
                "TEMPLATE_NOT_FOUND",
                "template-not-found",
                TemplateFailureCodes.NotFoundMessage,
                Now,
                TestContext.Current.CancellationToken);

        public void SetBaseline(string? baseline) => Jobs.SetBaseline(ReportJobId, baseline);
    }


}
