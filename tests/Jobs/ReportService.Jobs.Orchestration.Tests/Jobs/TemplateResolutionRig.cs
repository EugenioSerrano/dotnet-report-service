using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
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
/// One job over one upload, the record stores the steps read and write, the clock the
/// test moves, and the engine's host around the activity one case runs.
/// </summary>
internal sealed class Rig : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private const string Template = "invoice-energy";

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
        PageCodes = new PageCodeDeclarationActivities(Jobs, Versions, _loggerFactory);
    }

    public ManualTimeProvider Clock { get; }

    public InMemoryUploadStore Uploads { get; }

    public InMemoryJobStore Jobs { get; }

    public InMemoryTemplateVersionStore Versions { get; }

    public LogCapture Logs => _logs;

    public TemplateResolutionActivities Activities { get; }

    public PageCodeDeclarationActivities PageCodes { get; }

    public string ReportJobId { get; private set; } = string.Empty;

    public static async Task<Rig> CreateAsync(string? version, string? culture = null, string? timeZone = null)
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
            [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
            Consumer,
            uploadId,
            Template,
            version is null ? TemplateReferenceForm.ByName : TemplateReferenceForm.Exact,
            version,
            ReportService.Testing.AcceptanceIdentity.For(uploadId))
        {
            Culture = culture,
            TimeZone = timeZone,
        };
        Assert.IsType<JobAcceptanceResult.Accepted>(await rig.Jobs.AcceptAsync(acceptance, Now, cancellationToken));
        rig.ReportJobId = acceptance.ReportJobId;

        return rig;
    }

    /// <summary>Records a taken version, as the take would have left it before the job ran.</summary>
    /// <param name="version">The version that was taken.</param>
    /// <param name="owner">The consumer it belongs to.</param>
    /// <param name="shared">Whether every consumer may use it.</param>
    /// <param name="localeCulture">The culture the package declares as its own, when it declares one.</param>
    /// <param name="localeTimeZone">The time zone the package declares as its own, when it declares one.</param>
    public void Take(
        string version,
        string owner = Consumer,
        bool shared = false,
        string? localeCulture = null,
        string? localeTimeZone = null) =>
        Versions.Seed(TemplateVersionRecords.Sample(
            templateId: Template,
            version: version,
            owner: owner,
            shared: shared,
            localeCulture: localeCulture,
            localeTimeZone: localeTimeZone));

    public Task<PrepareTemplateResult> PrepareAsync() =>
        new ActivityEnvironment().RunAsync(() => Activities.PrepareTemplateAsync(new PrepareTemplateInput(ReportJobId)));

    public Task<TemplatePinResult> PinAsync(
        string version,
        bool taken,
        string? localeCulture = null,
        string? localeTimeZone = null)
    {
        if (taken)
        {
            Versions.Seed(TemplateVersionRecords.Sample(
                templateId: Template,
                version: version,
                owner: Consumer,
                localeCulture: localeCulture,
                localeTimeZone: localeTimeZone));
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
