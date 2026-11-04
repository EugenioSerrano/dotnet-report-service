extern alias orchestration;
extern alias pdf;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Worker;
using Temporalio.Workflows;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using PrepareTemplateInput = orchestration::ReportService.Workers.Orchestration.Jobs.PrepareTemplateInput;
using PrepareTemplateResult = orchestration::ReportService.Workers.Orchestration.Jobs.PrepareTemplateResult;
using ResolveTemplateVersionStepResult = orchestration::ReportService.Workers.Orchestration.Jobs.ResolveTemplateVersionStepResult;
using TakeTemplateVersionStepResult = orchestration::ReportService.Workers.Orchestration.Jobs.TakeTemplateVersionStepResult;
using TemplateResolutionActivities = orchestration::ReportService.Workers.Orchestration.Jobs.TemplateResolutionActivities;
using TemplateStepActivityOptions = orchestration::ReportService.Workers.Orchestration.Jobs.TemplateStepActivityOptions;
using TemplateStepContracts = orchestration::ReportService.Workers.Orchestration.Jobs.TemplateStepContracts;
using ResolveTemplateVersionResult = pdf::ReportService.Workers.Pdf.Templates.ResolveTemplateVersionResult;
using TakeTemplateVersionActivity = pdf::ReportService.Workers.Pdf.Templates.TakeTemplateVersionActivity;
using TakeTemplateVersionInput = pdf::ReportService.Workers.Pdf.Templates.TakeTemplateVersionInput;
using TakeTemplateVersionResult = pdf::ReportService.Workers.Pdf.Templates.TakeTemplateVersionResult;
using TakeTemplateVersionActivityOptions = pdf::ReportService.Workers.Pdf.Templates.TakeTemplateVersionActivityOptions;
using TemplateResolutionActivityOptions = pdf::ReportService.Workers.Pdf.Templates.TemplateResolutionActivityOptions;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The template of a report job, from the request to the pin, against real dependencies: one
/// PostgreSQL 18.6 database brought to the expected version by the product's migrations, one
/// Temporal 1.9.1 engine, one storage root, and the template source served over real HTTP on a
/// loopback port, seeded with the delivered fixture families. The API, the orchestration worker
/// and the PDF worker are hosted in this process on their own registrations, so what answers is
/// the product's own graph.
/// <para>
/// Every case submits through the API and reads the outcome from the store: the reference as
/// written, the baseline recorded at acceptance, the pin with its commit, package hash, engine,
/// schema and instant, and the job's failure with the code, reason and message a consumer acts
/// on. Printing is not needed: the pin is committed by the template step, before the delivered
/// data step reads it, so a case reads the pin while the job may still be running.
/// </para>
/// <para>
/// The case of a syntax error inside a part (<c>html/row.html.sbn</c>) is <b>not</b> run here
/// and is carried, never waived: the take registers no content checks, so such a package is not
/// refused yet. What this class proves is the job-level half - a take's <c>TEMPLATE_INVALID</c>
/// refusal fails the job with its code and message and nothing is taken.
/// </para>
/// </summary>
public sealed class JobTemplateResolutionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int GrpcPort = 7233;

    private const string ConsumerA = "consumer-a";

    private const string Owner = ByNameFixtures.Owner;

    private const string RepositoryOwner = ByNameFixtures.Other;

    private const string Template = ByNameFixtures.Template;

    private const string Pilot = "pilot-grouped";

    private const string ForeignTemplate = ByNameFixtures.ForeignTemplate;

    private const string PreReleaseTemplate = ByNameFixtures.PreReleaseTemplate;

    private static readonly TimeSpan EngineStartDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PinDeadline = TimeSpan.FromSeconds(90);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly LogCapture _logs = new();

    private readonly int _enginePort = FreePort();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-template-resolution-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private IContainer? _engine;

    private TemplateSourceHost? _source;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<OrchestrationWorker>? _orchestration;

    private WebApplicationFactory<PdfWorker>? _pdf;

    private TemporalClient? _client;

    private TemporalWorker? _testWorker;

    private readonly CancellationTokenSource _testWorkerStop = new();

    private Task? _testWorkerRun;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobTemplateResolutionTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);

        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = ContainerSmoke.Builder(_engineImage, "temporal-template-resolution")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_enginePort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _engineImage, cancellationToken);
        await Wait.UntilAsync(
            $"Temporal gRPC ({_engineImage.Reference}) on port {_enginePort}",
            async token =>
            {
                var probe = await TemporalClient.ConnectAsync(
                    new TemporalClientConnectOptions($"127.0.0.1:{_enginePort}") { Namespace = "default" });
                return await probe.Connection.CheckHealthAsync(options: new RpcOptions { CancellationToken = token });
            },
            EngineStartDeadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);
        _client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_enginePort}") { Namespace = "default" }).WaitAsync(cancellationToken);

        var stub = new GitHubTemplateSourceStub();
        ByNameFixtures.Seed(stub, ByNameFixtures.First, ByNameFixtures.Second, ByNameFixtures.HighestRelease);
        stub.SeedPackage(Template, ByNameFixtures.PreRelease, ByNameFixtures.Valid(Template, ByNameFixtures.PreRelease));
        ByNameFixtures.SeedPreReleases(stub);
        ByNameFixtures.SeedForeign(stub, foreignVersion: "1.0.0", sharedVersion: ByNameFixtures.Shared);
        ByNameFixtures.SeedInvalid(stub, ByNameFixtures.Invalid);
        ByNameFixtures.SeedUnsupported(stub, ByNameFixtures.Unsupported);
        stub.SeedPackage(Pilot, "1.2.0", TemplatePackageFixtures.Valid(Pilot, "1.2.0", owner: ConsumerA, shared: true));
        _source = await TemplateSourceHost.StartAsync(stub, cancellationToken);

        _api = Api();
        _orchestration = Orchestration();
        _pdf = Pdf();
        await WaitForHealthyAsync(_api.CreateClient(), ReadyDeadline, cancellationToken);
        await WaitForHealthyAsync(_orchestration.CreateClient(), ReadyDeadline, cancellationToken);
        await WaitForHealthyAsync(_pdf.CreateClient(), ReadyDeadline, cancellationToken);

        _testWorker = new TemporalWorker(
            _client,
            new TemporalWorkerOptions("template-resolution-tests")
                .AddWorkflow<PrepareTemplateForTestWorkflow>()
                .AddWorkflow<TakeForTestWorkflow>());
        _testWorkerRun = _testWorker.ExecuteAsync(_testWorkerStop.Token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _testWorkerStop.CancelAsync();
        if (_testWorkerRun is not null)
        {
            try
            {
                await _testWorkerRun.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The test worker is stopped: that is how it ends.
            }
        }

        _testWorker?.Dispose();
        _testWorkerStop.Dispose();
        _logs.Dispose();
        foreach (var host in new IAsyncDisposable?[] { _pdf, _orchestration, _api })
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task AnUntakenExactVersionIsTakenAndPinnedWithTheValuesTheRecordFixed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var reportJobId = await AcceptedJobAsync(Owner, Template, ByNameFixtures.Second, cancellationToken);
        var pin = await PinnedAsync(reportJobId, cancellationToken);

        // The pin carries what the take fixed when it read the version, never a caller's value.
        var taken = await TakenAsync(Template, ByNameFixtures.Second, cancellationToken);
        Assert.NotNull(taken);
        Assert.Equal(ByNameFixtures.Second, pin.ResolvedVersion);
        Assert.Equal(taken!.CommitSha, pin.CommitSha);
        Assert.Equal(taken.PackageSha256, pin.PackageSha256);
        Assert.Equal(taken.Engine, TemplateEngineNames.ToStoredText(pin.Engine));
        Assert.Equal(taken.EngineVersion, pin.EngineVersion);
        Assert.Equal(taken.SchemaRef, pin.SchemaRef);
        Assert.True(pin.ResolvedAt > DateTimeOffset.MinValue);

        // The reference as the consumer wrote it is unchanged beside the pin.
        var reference = await ReferenceAsync(reportJobId, cancellationToken);
        Assert.NotNull(reference);
        Assert.Equal("EXACT", reference!.ReferenceForm);
        Assert.Equal(ByNameFixtures.Second, reference.RequestedVersion);
        Assert.Null(reference.BaselineVersion);
    }

    [Fact]
    public async Task ATakenVersionIsBoundWithoutOneRequestToTheSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await AcceptedJobAsync(Owner, Template, ByNameFixtures.Second, cancellationToken);
        var firstPin = await PinnedAsync(first, cancellationToken);
        _source!.Stub.Stop();
        var requests = _source.Stub.RequestCount;

        var second = await AcceptedJobAsync(Owner, Template, ByNameFixtures.Second, cancellationToken);
        var secondPin = await PinnedAsync(second, cancellationToken);

        // A taken version is bound by the store alone: the source is never read for it.
        Assert.Equal(requests, _source.Stub.RequestCount);
        Assert.Equal(firstPin.ResolvedVersion, secondPin.ResolvedVersion);
        Assert.Equal(firstPin.CommitSha, secondPin.CommitSha);
        Assert.Equal(firstPin.PackageSha256, secondPin.PackageSha256);
    }

    [Fact]
    public async Task AnAbsentVersionAndAnAbsentTemplateFailTheJobAsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        foreach (var (templateId, version) in new[] { (Template, "9.9.9"), ("no-such-template", "1.0.0") })
        {
            var reportJobId = await AcceptedJobAsync(ConsumerA, templateId, version, cancellationToken);
            var state = await FailedAsync(reportJobId, cancellationToken);

            Assert.Equal(TemplateFailureCodes.NotFound, state.ErrorCode);
            Assert.Equal(TemplateFailureCodes.NotFoundReason, state.ErrorReason);

            // The message is the take's own text about the version it could not find, never a
            // value the consumer sent.
            Assert.Equal("The template version was not found.", state.ErrorMessage);

            // The output a consumer polls carries the same code as the job.
            var output = await OutputAsync(reportJobId, cancellationToken);
            Assert.Equal("FAILED", output!.Status);
            Assert.Equal(TemplateFailureCodes.NotFound, output.ErrorCode);
        }
    }

    [Fact]
    public async Task ARangeAndABranchAreRefusedBeforeAJobExists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var jobs = await JobCountAsync(cancellationToken);
        using var api = _api!.CreateClient();

        foreach (var version in new[] { "^1.0.0", "main" })
        {
            using var response = await SubmitAsync(api, ConsumerA, Template, version, await ReadyUploadAsync(cancellationToken), cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ErrorCodes.TemplateNotFound, await CodeAsync(response, cancellationToken));
        }

        // A reference that can never be taken costs nothing: no job, no intent, no taken version.
        Assert.Equal(jobs, await JobCountAsync(cancellationToken));
        Assert.Null(await TakenAsync(Template, "^1.0.0", cancellationToken));
        Assert.Null(await TakenAsync(Template, "main", cancellationToken));
    }

    [Fact]
    public async Task AnotherConsumersVersionIsIndistinguishableFromAnAbsentOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Untaken, another consumer's version is absent to this consumer: the take refuses it
        // before any of the package's content is looked at, and nothing is written.
        var refused = await AcceptedJobAsync(ConsumerA, ForeignTemplate, "1.0.0", cancellationToken);
        var state = await FailedAsync(refused, cancellationToken);
        Assert.Equal(TemplateFailureCodes.NotFound, state.ErrorCode);
        Assert.Equal("The template version was not found.", state.ErrorMessage);
        Assert.Null(await TakenAsync(ForeignTemplate, "1.0.0", cancellationToken));

        // Its owner takes it, and from then on the acceptance answers this consumer at once,
        // without creating a job that could only fail.
        var ownerJob = await AcceptedJobAsync(RepositoryOwner, ForeignTemplate, "1.0.0", cancellationToken);
        var ownerPin = await PinnedAsync(ownerJob, cancellationToken);
        Assert.Equal("1.0.0", ownerPin.ResolvedVersion);

        var jobs = await JobCountAsync(cancellationToken);
        using (var api = _api!.CreateClient())
        {
            using var response = await SubmitAsync(api, ConsumerA, ForeignTemplate, "1.0.0", await ReadyUploadAsync(cancellationToken), cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ErrorCodes.TemplateNotFound, await CodeAsync(response, cancellationToken));
        }

        Assert.Equal(jobs, await JobCountAsync(cancellationToken));

        // The shared sibling of the same family is every consumer's to use.
        var shared = await AcceptedJobAsync(ConsumerA, ForeignTemplate, ByNameFixtures.Shared, cancellationToken);
        var sharedPin = await PinnedAsync(shared, cancellationToken);
        Assert.Equal(ByNameFixtures.Shared, sharedPin.ResolvedVersion);
    }

    [Fact]
    public async Task AVersionTheTakeRefusesFailsTheJobAndNoOtherVersionIsUsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // A folder whose name is not the manifest's version, and a manifest that names no
        // owner: both are the take's own structural refusals, and both end the job with the
        // take's message. The file-naming half (a syntax error inside html/row.html.sbn) is
        // carried: the take registers no content checks, so it is not attempted here.
        _source!.Stub.SeedPackage(Pilot, "1.3.0", TemplatePackageFixtures.FolderDoesNotMatchTheVersion(Pilot, "1.3.0", "1.3.1"));
        _source.Stub.SeedPackage(Pilot, "1.5.0", TemplatePackageFixtures.OwnerMissing(Pilot, "1.5.0"));

        foreach (var (version, expected) in new[] { ("1.3.0", "1.3.1"), ("1.5.0", "owner") })
        {
            var reportJobId = await AcceptedJobAsync(Owner, Pilot, version, cancellationToken);
            var state = await FailedAsync(reportJobId, cancellationToken);

            Assert.Equal(TemplateFailureCodes.Invalid, state.ErrorCode);
            Assert.Contains(expected, state.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(await TakenAsync(Pilot, version, cancellationToken));
        }

        // The valid sibling of the same template was never substituted.
        Assert.Null(await ReferencePinnedToAsync("1.2.0", cancellationToken));
    }

    [Fact]
    public async Task AnUnreachableSourceFailsAnUntakenExactVersionAndPinsTheByNameBaseline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // A version taken before the job is accepted is the baseline a by-name job may fall
        // back to, and the acceptance records it then.
        var baselineJob = await AcceptedJobAsync(Owner, Template, ByNameFixtures.First, cancellationToken);
        await PinnedAsync(baselineJob, cancellationToken);

        // The by-name job is accepted while the orchestration worker is stopped, so its run
        // cannot consume the source before the baseline it recorded is read back: the baseline
        // is 1.0.0, the only version taken at that instant.
        await _orchestration!.DisposeAsync();
        var byName = await AcceptedJobAsync(Owner, Template, version: null, cancellationToken);
        var reference = await ReferenceAsync(byName, cancellationToken);
        Assert.Equal(ByNameFixtures.First, reference!.BaselineVersion);

        // A newer version is taken afterwards, by the take of another job, through the PDF
        // worker's own activity - the queue serves it whether the orchestration worker runs.
        await TakeAsync(Template, ByNameFixtures.HighestRelease, Owner, cancellationToken);
        Assert.NotNull(await TakenAsync(Template, ByNameFixtures.HighestRelease, cancellationToken));

        // The source is unreachable when the step finally runs: the baseline wins, never the
        // version taken later.
        _source!.Stub.Stop();
        _orchestration = Orchestration();
        await WaitForHealthyAsync(_orchestration.CreateClient(), ReadyDeadline, cancellationToken);

        var pin = await PinnedAsync(byName, cancellationToken);
        Assert.Equal(ByNameFixtures.First, pin.ResolvedVersion);
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("using the version", StringComparison.Ordinal)
                    && line.Contains(ByNameFixtures.First, StringComparison.Ordinal));

        // An untaken exact version fails with the code that says it may be submitted again.
        var exact = await AcceptedJobAsync(Owner, Template, ByNameFixtures.Second, cancellationToken);
        var exactState = await FailedAsync(exact, cancellationToken);
        Assert.Equal(TemplateFailureCodes.SourceUnavailable, exactState.ErrorCode);
        Assert.Equal("The template source could not be read. The version may be retried later.", exactState.ErrorMessage);

        // By name with nothing taken at acceptance: there is nothing to fall back to.
        var neverTaken = await AcceptedJobAsync(ConsumerA, "invoice-energy-untaken", version: null, cancellationToken);
        var neverTakenState = await FailedAsync(neverTaken, cancellationToken);
        Assert.Equal(TemplateFailureCodes.SourceUnavailable, neverTakenState.ErrorCode);
    }

    [Fact]
    public async Task AByNameJobTakesTheHighestReleaseAndAPreReleaseOnlyTemplateFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The pre-release is higher by precedence and is never chosen by name.
        var byName = await AcceptedJobAsync(Owner, Template, version: null, cancellationToken);
        var pin = await PinnedAsync(byName, cancellationToken);
        Assert.Equal(ByNameFixtures.HighestRelease, pin.ResolvedVersion);

        // An exact reference to the pre-release is served as the version the consumer named.
        var preRelease = await AcceptedJobAsync(Owner, Template, ByNameFixtures.PreRelease, cancellationToken);
        var preReleasePin = await PinnedAsync(preRelease, cancellationToken);
        Assert.Equal(ByNameFixtures.PreRelease, preReleasePin.ResolvedVersion);

        // A template that holds only pre-releases has no release to resolve to.
        var refused = await AcceptedJobAsync(Owner, PreReleaseTemplate, version: null, cancellationToken);
        var state = await FailedAsync(refused, cancellationToken);
        Assert.Equal(TemplateFailureCodes.Invalid, state.ErrorCode);
    }

    [Fact]
    public async Task ARetryAfterThePinAndARestartedWorkerNeverMoveTheVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var reportJobId = await AcceptedJobAsync(Owner, Template, version: null, cancellationToken);
        var first = await PinnedAsync(reportJobId, cancellationToken);
        Assert.Equal(ByNameFixtures.HighestRelease, first.ResolvedVersion);

        // A newer version is published and taken by another job after this one was pinned.
        _source!.Stub.SeedPackage(Template, "1.11.0", ByNameFixtures.Valid(Template, "1.11.0"));
        var newer = await AcceptedJobAsync(Owner, Template, "1.11.0", cancellationToken);
        await PinnedAsync(newer, cancellationToken);

        // The PDF worker is stopped and started again, and the step of the first job is asked
        // once more on the orchestration queue, as a retried activity would ask it.
        await _pdf!.DisposeAsync();
        _pdf = Pdf();
        await WaitForHealthyAsync(_pdf.CreateClient(), ReadyDeadline, cancellationToken);

        var again = await PrepareAgainAsync(reportJobId, cancellationToken);
        var second = await PinnedAsync(reportJobId, cancellationToken);

        Assert.Equal("Pinned", again.Outcome.ToString());
        Assert.Equal(first.ResolvedVersion, second.ResolvedVersion);
        Assert.Equal(first.ResolvedAt, second.ResolvedAt);
        Assert.Equal(first.PackageSha256, second.PackageSha256);
    }

    [Fact]
    public async Task TheTemplateCountersAndThePinSpanNameTheJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var spans = new SpanCapture();
        using var counters = new CounterCapture();

        var reportJobId = await AcceptedJobAsync(Owner, Template, ByNameFixtures.Second, cancellationToken);
        await PinnedAsync(reportJobId, cancellationToken);
        await Wait.UntilAsync(
            "the template counters of the job's step",
            _ => Task.FromResult(counters.HasTemplateResolution(reportJobId: null)),
            PinDeadline,
            TimeSpan.FromMilliseconds(100),
            cancellationToken);

        // The counter names the form, the path and the code - constants only.
        var resolution = Assert.Single(counters.TemplateResolutions());
        Assert.Equal("exact", resolution.Tag("form"));
        Assert.Equal("took", resolution.Tag("path"));
        Assert.Equal(OrchestrationMetrics.NoCode, resolution.Tag("code"));

        // The pin's span carries the job it belongs to, the form, the path and the version.
        var pin = Assert.Single(spans.Of("template.pin"));
        Assert.Equal(reportJobId, pin.GetTagItem(JobContext.ActivityTagName) as string);
        Assert.Equal("took", pin.GetTagItem("template.path") as string);
        Assert.Equal(ByNameFixtures.Second, pin.GetTagItem("template.version") as string);
    }

    [Fact]
    public void TheMirrorRecordsAndTheOptionValuesEqualTheDeliveredOnes()
    {
        // The caller's records are the delivered results, member for member: the engine writes
        // the PDF worker's result and the orchestration worker reads it through its own record.
        var taken = new TakeTemplateVersionResult(
            Template,
            ByNameFixtures.Second,
            Owner,
            true,
            "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
            new string('a', 64),
            1_024,
            "SCRIBAN",
            "7.5.1",
            "schema.json",
            TemplateStepContracts.TakeOutcomes.Taken);
        var takenMirror = Assert.IsType<TakeTemplateVersionStepResult>(
            RoundTrip(taken, typeof(TakeTemplateVersionStepResult)));
        Assert.Equal(taken.TemplateId, takenMirror.TemplateId);
        Assert.Equal(taken.Version, takenMirror.Version);
        Assert.Equal(taken.OwnerConsumerId, takenMirror.OwnerConsumerId);
        Assert.Equal(taken.Shared, takenMirror.Shared);
        Assert.Equal(taken.CommitSha, takenMirror.CommitSha);
        Assert.Equal(taken.PackageSha256, takenMirror.PackageSha256);
        Assert.Equal(taken.PackageSize, takenMirror.PackageSize);
        Assert.Equal(taken.Engine, takenMirror.Engine);
        Assert.Equal(taken.EngineVersion, takenMirror.EngineVersion);
        Assert.Equal(taken.SchemaRef, takenMirror.SchemaRef);
        Assert.Equal(taken.Outcome, takenMirror.Outcome);

        var resolved = new ResolveTemplateVersionResult(
            Template,
            ByNameFixtures.HighestRelease,
            Owner,
            true,
            "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
            new string('b', 64),
            2_048,
            "SCRIBAN",
            "7.5.1",
            "schema.json",
            TemplateStepContracts.ResolutionOutcomes.ResolvedFromTakenCopy);
        var resolvedMirror = Assert.IsType<ResolveTemplateVersionStepResult>(
            RoundTrip(resolved, typeof(ResolveTemplateVersionStepResult)));
        Assert.Equal(resolved.TemplateId, resolvedMirror.TemplateId);
        Assert.Equal(resolved.Version, resolvedMirror.Version);
        Assert.Equal(resolved.OwnerConsumerId, resolvedMirror.OwnerConsumerId);
        Assert.Equal(resolved.Shared, resolvedMirror.Shared);
        Assert.Equal(resolved.CommitSha, resolvedMirror.CommitSha);
        Assert.Equal(resolved.PackageSha256, resolvedMirror.PackageSha256);
        Assert.Equal(resolved.PackageSize, resolvedMirror.PackageSize);
        Assert.Equal(resolved.Engine, resolvedMirror.Engine);
        Assert.Equal(resolved.EngineVersion, resolvedMirror.EngineVersion);
        Assert.Equal(resolved.SchemaRef, resolvedMirror.SchemaRef);
        Assert.Equal(resolved.Outcome, resolvedMirror.Outcome);

        // Every value of the caller's execution settings equals the delivered factory's.
        var take = TakeTemplateVersionActivityOptions.Create();
        var takeMirror = orchestration::ReportService.Workers.Orchestration.Jobs.TakeTemplateStepOptions.Create();
        Assert.Equal(take.TaskQueue, takeMirror.TaskQueue);
        Assert.Equal(take.HeartbeatTimeout, takeMirror.HeartbeatTimeout);
        Assert.Equal(take.StartToCloseTimeout, takeMirror.StartToCloseTimeout);
        Assert.Equal(take.RetryPolicy!.InitialInterval, takeMirror.RetryPolicy!.InitialInterval);
        Assert.Equal(take.RetryPolicy.BackoffCoefficient, takeMirror.RetryPolicy.BackoffCoefficient);
        Assert.Equal(take.RetryPolicy.MaximumInterval, takeMirror.RetryPolicy.MaximumInterval);
        Assert.Equal(take.RetryPolicy.MaximumAttempts, takeMirror.RetryPolicy.MaximumAttempts);
        Assert.Equal(take.RetryPolicy.NonRetryableErrorTypes, takeMirror.RetryPolicy.NonRetryableErrorTypes);

        var resolve = TemplateResolutionActivityOptions.Create();
        var resolveMirror = orchestration::ReportService.Workers.Orchestration.Jobs.ResolveTemplateStepOptions.Create();
        Assert.Equal(resolve.TaskQueue, resolveMirror.TaskQueue);
        Assert.Equal(resolve.HeartbeatTimeout, resolveMirror.HeartbeatTimeout);
        Assert.Equal(resolve.StartToCloseTimeout, resolveMirror.StartToCloseTimeout);
        Assert.Equal(resolve.RetryPolicy!.InitialInterval, resolveMirror.RetryPolicy!.InitialInterval);
        Assert.Equal(resolve.RetryPolicy.BackoffCoefficient, resolveMirror.RetryPolicy.BackoffCoefficient);
        Assert.Equal(resolve.RetryPolicy.MaximumInterval, resolveMirror.RetryPolicy.MaximumInterval);
        Assert.Equal(resolve.RetryPolicy.MaximumAttempts, resolveMirror.RetryPolicy.MaximumAttempts);
        Assert.Equal(resolve.RetryPolicy.NonRetryableErrorTypes, resolveMirror.RetryPolicy.NonRetryableErrorTypes);
    }

    // The engine's own converter writes one side and reads the other, as it does in a run.
    private static object? RoundTrip(object value, Type readAs)
    {
        var payload = DataConverter.Default.PayloadConverter.ToPayload(value);
        return payload is null ? null : DataConverter.Default.PayloadConverter.ToValue(payload, readAs);
    }

    // The request the product's own operation answers, sent to a host built from the product's
    // own registrations, over a ready upload the database holds.
    private async Task<string> AcceptedJobAsync(
        string consumerId,
        string templateId,
        string? version,
        CancellationToken cancellationToken)
    {
        using var api = _api!.CreateClient();
        var uploadId = await ReadyUploadAsync(consumerId, cancellationToken);
        using var response = await SubmitAsync(api, consumerId, templateId, version, uploadId, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"{templateId} {version ?? "(by name)"} for {consumerId} had to be accepted; the answer was {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient api,
        string consumerId,
        string templateId,
        string? version,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var template = version is null
            ? $$"""{"id":"{{templateId}}"}"""
            : $$"""{"id":"{{templateId}}","version":"{{version}}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                $$"""{"template":{{template}},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        return await api.SendAsync(request, cancellationToken);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, ConsumerRegistry());
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });
    }

    private WebApplicationFactory<OrchestrationWorker> Orchestration() =>
        new WebApplicationFactory<OrchestrationWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_enginePort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private WebApplicationFactory<PdfWorker> Pdf() =>
        new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_enginePort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:ApiBaseUrl", _source!.BaseUrl.AbsoluteUri);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Repository", GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Branch", GitHubTemplateSourceStub.Branch);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:AllowInsecureLoopback", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private static string ConsumerRegistry() => $$"""
        { "consumers": [
            { "consumerId": "{{ConsumerA}}", "active": true },
            { "consumerId": "{{Owner}}", "active": true },
            { "consumerId": "{{RepositoryOwner}}", "active": true } ] }
        """;

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private static async Task WaitForHealthyAsync(HttpClient client, TimeSpan deadline, CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            "a host answering /health",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    // One upload in the state a job's data reference is judged in, driven through the record
    // store's own steps so every case starts from a state the service can reach.
    private async Task<string> ReadyUploadAsync(CancellationToken cancellationToken) =>
        await ReadyUploadAsync(ConsumerA, cancellationToken);

    private async Task<string> ReadyUploadAsync(string consumerId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var uploads = new PostgresUploadStore(_dataSource!);
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                consumerId,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(1)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, consumerId, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, consumerId, null, now, now, now.AddHours(1), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    consumerId,
                    ObjectKey.FromSegments("snapshots", uploadId, "a"),
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    new string('a', 64),
                    900,
                    new string('a', 64),
                    900,
                    now),
                now,
                cancellationToken));
        return uploadId;
    }

    private async Task<TemplatePin> PinnedAsync(string reportJobId, CancellationToken cancellationToken)
    {
        TemplatePin? pin = null;
        try
        {
            await Wait.UntilAsync(
                $"the template pin of {reportJobId}",
                async token =>
                {
                    pin = (await ReferenceAsync(reportJobId, token))?.Pin;
                    return pin is not null;
                },
                PinDeadline,
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            // The workers' own record of why the step did not finish: a step that keeps failing
            // is retried under its own policy, so the store alone would never say why.
            throw new Xunit.Sdk.XunitException(
                $"{reportJobId} was not pinned in {PinDeadline.TotalSeconds} s.{Environment.NewLine}"
                + string.Join(Environment.NewLine, _logs.Lines.TakeLast(60)));
        }

        return pin!;
    }

    private async Task<JobState> FailedAsync(string reportJobId, CancellationToken cancellationToken)
    {
        JobState? state = null;
        await Wait.UntilAsync(
            $"the job {reportJobId} to end",
            async token =>
            {
                state = await JobAsync(reportJobId, token);
                return state?.Status is "FAILED" or "SUCCEEDED" or "CANCELLED";
            },
            PinDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        Assert.Equal("FAILED", state!.Status);
        return state;
    }

    private async Task<TemplateReferenceRow?> ReferenceAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TemplateReferenceRow>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", template_id AS "TemplateId",
                   reference_form AS "ReferenceForm", requested_version AS "RequestedVersion",
                   baseline_version AS "BaselineVersion", resolved_version AS "ResolvedVersion",
                   commit_sha AS "CommitSha", package_sha256 AS "PackageSha256", engine AS "Engine",
                   engine_version AS "EngineVersion", schema_ref AS "SchemaRef", resolved_at AS "ResolvedAt"
              FROM report_service.template_references
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<TakenRow?> TakenAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TakenRow>(new CommandDefinition(
            """
            SELECT template_id AS "TemplateId", version AS "Version", commit_sha AS "CommitSha",
                   package_sha256 AS "PackageSha256", engine AS "Engine", engine_version AS "EngineVersion",
                   schema_ref AS "SchemaRef"
              FROM report_service.template_versions
             WHERE template_id = @TemplateId AND version = @Version
            """,
            new { TemplateId = templateId, Version = version },
            cancellationToken: cancellationToken));
    }

    private async Task<string?> ReferencePinnedToAsync(string version, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT report_job_id FROM report_service.template_references WHERE resolved_version = @Version",
            new { Version = version },
            cancellationToken: cancellationToken));
    }

    private async Task<JobState?> JobAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<JobState>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", status AS "Status", error_code AS "ErrorCode",
                   error_reason AS "ErrorReason", error_message AS "ErrorMessage", finished_at AS "FinishedAt"
              FROM report_service.report_jobs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<OutputState?> OutputAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<OutputState>(new CommandDefinition(
            """
            SELECT output_id AS "OutputId", status AS "Status", error_code AS "ErrorCode"
              FROM report_service.report_outputs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<long> JobCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.report_jobs",
            cancellationToken: cancellationToken));
    }

    private Task<PrepareTemplateResult> PrepareAgainAsync(string reportJobId, CancellationToken cancellationToken) =>
        _client!
            .ExecuteWorkflowAsync(
                (PrepareTemplateForTestWorkflow workflow) => workflow.RunAsync(reportJobId),
                new WorkflowOptions($"prepare-{Guid.NewGuid():N}", "template-resolution-tests"))
            .WaitAsync(cancellationToken);

    private Task<TakeTemplateVersionResult> TakeAsync(
        string templateId,
        string version,
        string consumerId,
        CancellationToken cancellationToken) =>
        _client!
            .ExecuteWorkflowAsync(
                (TakeForTestWorkflow workflow) => workflow.RunAsync(new TakeTemplateVersionInput(templateId, version, consumerId, null)),
                new WorkflowOptions($"take-{Guid.NewGuid():N}", "template-resolution-tests"))
            .WaitAsync(cancellationToken);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>One reference row, as the table holds it.</summary>
    private sealed class TemplateReferenceRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string TemplateId { get; set; } = string.Empty;

        public string ReferenceForm { get; set; } = string.Empty;

        public string? RequestedVersion { get; set; }

        public string? BaselineVersion { get; set; }

        public string? ResolvedVersion { get; set; }

        public string? CommitSha { get; set; }

        public string? PackageSha256 { get; set; }

        public string? Engine { get; set; }

        public string? EngineVersion { get; set; }

        public string? SchemaRef { get; set; }

        public DateTimeOffset? ResolvedAt { get; set; }

        public TemplatePin? Pin
        {
            get
            {
                if (ResolvedVersion is null || ResolvedAt is null
                    || !TemplateEngineNames.TryParseStoredText(Engine, out var engine))
                {
                    return null;
                }

                return new TemplatePin(
                    TemplateId,
                    ResolvedVersion,
                    CommitSha!,
                    PackageSha256!,
                    engine!.Value,
                    EngineVersion!,
                    SchemaRef!,
                    ResolvedAt.Value);
            }
        }
    }

    /// <summary>One taken version, as the table holds it.</summary>
    private sealed class TakenRow
    {
        public string TemplateId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string CommitSha { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string EngineVersion { get; set; } = string.Empty;

        public string SchemaRef { get; set; } = string.Empty;
    }

    /// <summary>One job, as the table holds it.</summary>
    private sealed class JobState
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }

        public string? ErrorReason { get; set; }

        public string? ErrorMessage { get; set; }

        public DateTime? FinishedAt { get; set; }
    }

    /// <summary>One output, as the table holds it.</summary>
    private sealed class OutputState
    {
        public string OutputId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }
    }

    /// <summary>Captures the spans the product's own activity source writes, in this process.</summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<Activity> _stopped = new();

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => string.Equals(source.Name, TelemetryDefaults.ActivitySourceName, StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _stopped.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Of(string name) =>
            [.. _stopped.Where(activity => string.Equals(activity.OperationName, name, StringComparison.Ordinal))];

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Captures the counters the product's own meter writes, in this process.</summary>
    private sealed class CounterCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly ConcurrentQueue<Measurement> _measurements = new();

        public CounterCapture()
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
                _measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray())));
            _listener.Start();
        }

        public IReadOnlyList<Measurement> TemplateResolutions() =>
            [.. _measurements.Where(measurement => string.Equals(
                measurement.Name,
                OrchestrationMetrics.TemplateResolutionsInstrumentName,
                StringComparison.Ordinal))];

        public bool HasTemplateResolution(string? reportJobId) => TemplateResolutions().Count > 0;

        public void Dispose() => _listener.Dispose();

        /// <summary>One measurement of one instrument.</summary>
        public sealed record Measurement(
            string Name,
            long Value,
            IReadOnlyList<KeyValuePair<string, object?>> Tags)
        {
            public string? Tag(string key) =>
                Tags.FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.Ordinal)).Value as string;
        }
    }
}

/// <summary>
/// The test's own caller: one workflow that asks the orchestration worker for one template
/// preparation, exactly as a retried activity of a running job would. It carries no product
/// behaviour of its own, and it runs on a queue of this process.
/// </summary>
[Workflow]
public sealed class PrepareTemplateForTestWorkflow
{
    /// <summary>Asks the orchestration queue to prepare one job's template once.</summary>
    /// <param name="reportJobId">The job whose reference is prepared.</param>
    [WorkflowRun]
    public Task<PrepareTemplateResult> RunAsync(string reportJobId) =>
        Workflow.ExecuteActivityAsync(
            (TemplateResolutionActivities activities) => activities.PrepareTemplateAsync(new PrepareTemplateInput(reportJobId)),
            new ActivityOptions
            {
                TaskQueue = TemplateStepActivityOptions.Queue,
                StartToCloseTimeout = TemplateStepActivityOptions.StartToClose,
                RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
            });
}

/// <summary>
/// The test's own caller of one take: it asks the PDF worker's queue for one version exactly as
/// a job's step would, so a case can arrange a taken version without a job of its own. It
/// carries no product behaviour, and its execution settings are the delivered factory's.
/// </summary>
[Workflow]
public sealed class TakeForTestWorkflow
{
    /// <summary>Takes one version on behalf of one consumer.</summary>
    /// <param name="input">The version to take and who asks for it.</param>
    [WorkflowRun]
    public Task<TakeTemplateVersionResult> RunAsync(TakeTemplateVersionInput input) =>
        Workflow.ExecuteActivityAsync(
            (TakeTemplateVersionActivity activity) => activity.RunAsync(input),
            TakeTemplateVersionActivityOptions.Create());
}
