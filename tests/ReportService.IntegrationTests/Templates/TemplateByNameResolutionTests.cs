extern alias pdf;

using System.Globalization;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Resolution;
using pdf::ReportService.Workers.Pdf.Templates;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The by-name resolution where it runs: on the PDF worker's queue, with the
/// template source served over real HTTP on a loopback port, PostgreSQL 18.6 with
/// the product's migrations, a real engine and a storage root the worker and this
/// process share. One test workflow calls <c>ResolveTemplateVersion</c> on
/// <c>report-pdf-rendering</c> for each case, and the case records what the job
/// would be told, what the resolver logged and what the store now holds.
/// <para>
/// Every version this class <b>takes</b> is a package the product's own rules
/// accept. The checks of a later change are not registered yet, so a "newer invalid
/// version" is one the take's own structural rules refuse — a manifest that
/// declares another version — and never a package written to be refused by checks
/// this build does not run.
/// </para>
/// <para>
/// The deadline case starts a worker of its own on a five-second resolution bound —
/// the shortest the setting allows — and that worker is the only one polling the
/// resolution queue for the case: this class's four-minute worker is started when a
/// case first needs it, and the deadline case never does. The case seeds thousands
/// of versions the take refuses, so the walk costs several times the bound and the
/// bound is always reached; it asserts that reaching the bound is never reported as
/// invalidity, that the answer is the copy already taken, and that the resolution's
/// own record names the deadline. A run that finished the walk first would answer
/// from a candidate and fail by name rather than pass with nothing checked.
/// </para>
/// </summary>
public sealed class TemplateByNameResolutionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Template = ByNameFixtures.Template;
    private const string TestQueue = "template-by-name-tests";
    private const string DeadlineQueue = "template-by-name-deadline-tests";
    private const string ResolutionDeadline = "00:04:00";
    private const string ShortResolutionDeadline = "00:00:05";

    /// <summary>
    /// The refused versions the deadline case seeds over the taken one. Each refusal
    /// is a real read of a package the take refuses, so the walk costs what the
    /// source costs: 400 of them measured 4.9 to 8.6 seconds on this suite's own
    /// runs, against a five-second bound. 4,000 keeps the walk above the bound even
    /// at the fastest refusal this suite has recorded (7 ms each - 28 seconds, more
    /// than five times the bound), so the bound is reached on every run instead of
    /// the walk finishing first.
    /// </summary>
    private const int RefusedVersions = 4000;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-by-name-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    private readonly LogCapture _logs = new();

    private EngineStart.Engine? _engine;
    private int _hostPort;
    private string _connectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private TemplateSourceClass? _source;
    private SwitchableStore? _sourceStore;
    private WebApplicationFactory<PdfWorker>? _worker;
    private TemporalWorker? _testWorker;
    private readonly CancellationTokenSource _testWorkerStop = new();
    private Task? _testWorkerRun;
    private TemporalClient? _client;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public TemplateByNameResolutionTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            await OpenAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);

        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = _fixture.Application(database);
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-template-by-name", cancellationToken);
        _hostPort = _engine.HostPort;

        Directory.CreateDirectory(StorageRoot());
        _source = await TemplateSourceClass.CreateAsync(StorageRoot());
        _sourceStore = new SwitchableStore(_source.Store);

        _client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" }).WaitAsync(cancellationToken);

        _testWorker = new TemporalWorker(
            _client,
            new TemporalWorkerOptions(TestQueue).AddWorkflow<ResolveVersionForTestWorkflow>().AddWorkflow<TakeVersionForTestWorkflow>());
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
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
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
    public async Task ANewerValidVersionIsTakenAndRecorded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.First, ByNameFixtures.Second);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ResolveTemplateVersionOutcomes.Resolved, result.Outcome);
        Assert.Equal(ByNameFixtures.Second, result.Version);
        var row = await RowAsync(Template, ByNameFixtures.Second, cancellationToken);
        Assert.Null(result.CommitSha);
        Assert.Equal(result.SourceDescriptorSha256, row.SourceDescriptorSha256);
        Assert.Equal(result.PackageSha256, row.PackageSha256);
    }

    [Fact]
    public async Task ANewerInvalidVersionIsSkippedAndTheOlderTakenOneUsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.First, ByNameFixtures.Invalid);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ResolveTemplateVersionOutcomes.Resolved, result.Outcome);
        Assert.Equal(ByNameFixtures.First, result.Version);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.Invalid, cancellationToken));
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("template.by_name.skipped", StringComparison.Ordinal)
                && line.Contains(ByNameFixtures.Invalid, StringComparison.Ordinal)
                && line.Contains("does not match the version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingValidEndsWithTemplateInvalidNamingTheNewestVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedInvalidAsync(ByNameFixtures.First, ByNameFixtures.Second);

        var cause = await AssertRefusedAsync(ByNameFixtures.InvalidTemplate, TemplateResolutionCodes.TemplateInvalid, cancellationToken);

        Assert.True(cause.NonRetryable);
        Assert.Contains(ByNameFixtures.Second, cause.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ByNameFixtures.First, cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATruncatedTemplateFolderEndsWithTemplateInvalidAndTakesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.First);
        

        var cause = await AssertRefusedAsync(Template, TemplateResolutionCodes.TemplateInvalid, cancellationToken);

        Assert.True(cause.NonRetryable);
        Assert.Contains("too many entries", cause.Message, StringComparison.Ordinal);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.First, cancellationToken));
    }

    [Fact]
    public async Task APreReleaseIsNeverChosenByName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.First, ByNameFixtures.Second, ByNameFixtures.PreRelease);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.Second, result.Version);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.PreRelease, cancellationToken));
    }

    [Fact]
    public async Task ATemplateWithOnlyPreReleasesEndsWithTemplateInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPreReleasesAsync();

        var cause = await AssertRefusedAsync(ByNameFixtures.PreReleaseTemplate, TemplateResolutionCodes.TemplateInvalid, cancellationToken);

        Assert.Contains("has no release version", cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSourceDownUsesTheLatestReleaseAlreadyTaken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.First);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);
        _sourceStore!.Fail = true;

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ResolveTemplateVersionOutcomes.ResolvedFromTakenCopy, result.Outcome);
        Assert.Equal(ByNameFixtures.First, result.Version);
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("template.by_name.unchecked", StringComparison.Ordinal)
                && line.Contains("could not be checked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSourceDownWithNothingTakenEndsWithSourceUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.Unsupported, ByNameFixtures.HighestRelease);
        _sourceStore!.Fail = true;

        var cause = await AssertRefusedAsync(Template, TemplateResolutionCodes.TemplateSourceUnavailable, cancellationToken);

        Assert.False(cause.NonRetryable);
        Assert.Equal(TemplateVersionResolver.SourceUnavailableReason, cause.Message);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.HighestRelease, cancellationToken));
    }

    [Fact]
    public async Task TheOrderIsPrecedenceAndNotText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _source!.PublishAsync(Template, ByNameFixtures.Invalid, ByNameFixtures.Valid(Template, ByNameFixtures.Invalid));
        await _source!.PublishAsync(Template, ByNameFixtures.HighestRelease, ByNameFixtures.Valid(Template, ByNameFixtures.HighestRelease));

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        // "1.2.0" is the higher string; 1.10.0 is the higher version.
        Assert.Equal(ByNameFixtures.HighestRelease, result.Version);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.Invalid, cancellationToken));
    }

    [Fact]
    public async Task TheHigherFullVersionWinsWhenNeitherIsTaken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedEqualPrecedenceAsync();

        var result = await ResolveAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.EqualPrecedence[1], result.Version);
        Assert.Null(await RowOrNullAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.EqualPrecedence[0], cancellationToken));
    }

    [Fact]
    public async Task TheVersionAlreadyTakenWinsOnEqualPrecedence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedEqualPrecedenceAsync();
        await TakeAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.EqualPrecedence[0], cancellationToken);

        var result = await ResolveAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.EqualPrecedence[0], result.Version);
        Assert.Null(await RowOrNullAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.EqualPrecedence[1], cancellationToken));
    }

    [Fact]
    public async Task AVersionAddedBetweenTwoResolutionsIsSeenOnlyByTheSecond()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.First);

        var first = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);
        await _source!.PublishAsync(Template, ByNameFixtures.Second, ByNameFixtures.Valid(Template, ByNameFixtures.Second));
        var second = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.First, first.Version);
        Assert.Equal(ByNameFixtures.Second, second.Version);
    }

    [Fact]
    public async Task ATakenVersionRemovedFromTheBranchIsStillUsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.First, ByNameFixtures.Second);
        await TakeAsync(Template, ByNameFixtures.Second, cancellationToken);
        await _source!.RemoveAsync(Template, ByNameFixtures.Second);

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.Second, result.Version);
        Assert.NotNull(await RowOrNullAsync(Template, ByNameFixtures.Second, cancellationToken));
    }

    [Fact]
    public async Task AnotherConsumersNonSharedVersionIsExcludedAndASharedOneIsUsable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedForeignAsync();

        var result = await ResolveAsync(ByNameFixtures.ForeignTemplate, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.Shared, result.Version);
        Assert.Null(await RowOrNullAsync(ByNameFixtures.ForeignTemplate, ByNameFixtures.Second, cancellationToken));
    }

    [Fact]
    public async Task OnlyAnotherConsumersVersionAnswersTheConstantNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _source!.PublishAsync(
            ByNameFixtures.ForeignTemplate,
            ByNameFixtures.Second,
            ByNameFixtures.Valid(ByNameFixtures.ForeignTemplate, ByNameFixtures.Second, ByNameFixtures.Other, shared: false));

        var cause = await AssertRefusedAsync(ByNameFixtures.ForeignTemplate, TemplateResolutionCodes.TemplateNotFound, cancellationToken);

        Assert.True(cause.NonRetryable);
        Assert.DoesNotContain(ByNameFixtures.Second, cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionTheDeploymentCannotRenderIsSkipped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(ByNameFixtures.First, ByNameFixtures.Unsupported);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.First, result.Version);
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("template.by_name.skipped", StringComparison.Ordinal)
                && line.Contains(ByNameFixtures.Unsupported, StringComparison.Ordinal)
                && line.Contains("not one this deployment renders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATemplateWhoseVersionsAreAllUnsupportedEndsWithTemplateInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedUnsupportedAsync(ByNameFixtures.First, ByNameFixtures.Second);

        var cause = await AssertRefusedAsync(ByNameFixtures.UnsupportedTemplate, TemplateResolutionCodes.TemplateInvalid, cancellationToken);

        Assert.Contains(ByNameFixtures.Second, cause.Message, StringComparison.Ordinal);
        Assert.Contains("not one this deployment renders", cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeadlineReachedBeforeADecisionIsNeverReportedAsInvalidity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The version the fallback answers with, seeded before anything takes it.
        await SeedAsync(ByNameFixtures.First);

        // The short-bound host is the only worker on the resolution queue: the
        // class's four-minute host is never started for this case, so the bound
        // that cuts the walk can only be this worker's five seconds.
        await using var shortWorker = await ShortWorkerAsync(cancellationToken);
        await TakeAsync(Template, ByNameFixtures.First, shortWorker.Queue, cancellationToken);

        for (var index = 0; index < RefusedVersions; index++)
        {
            var version = $"1.1.{index}";
            await _source!.PublishAsync(Template, version, ByNameFixtures.InvalidPackage(Template, version));
        }

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, shortWorker.Queue, cancellationToken);

        Assert.Equal(ResolveTemplateVersionOutcomes.ResolvedFromTakenCopy, result.Outcome);
        Assert.Equal(ByNameFixtures.First, result.Version);
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("template.by_name.unchecked", StringComparison.Ordinal)
                && line.Contains("kind deadline", StringComparison.Ordinal));
    }

    /// <summary>The application failure a resolution ends its job with, by the code the case names.</summary>
    private async Task<ApplicationFailureException> AssertRefusedAsync(
        string templateId,
        string code,
        CancellationToken cancellationToken)
    {
        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => ResolveAsync(templateId, ByNameFixtures.Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(code, cause.ErrorType);
        return cause;
    }

    private static ApplicationFailureException ApplicationFailure(WorkflowFailedException failure)
    {
        for (Exception? exception = failure; exception is not null; exception = exception.InnerException)
        {
            if (exception is ApplicationFailureException application)
            {
                return application;
            }
        }

        throw new Xunit.Sdk.XunitException($"the workflow failed without an application failure: {failure.Message}");
    }

    private async Task<TakeTemplateVersionResult> TakeAsync(
        string templateId,
        string version,
        CancellationToken cancellationToken)
    {
        await LongWorkerAsync(cancellationToken);
        return await TakeAsync(templateId, version, TestQueue, cancellationToken);
    }

    private async Task<TakeTemplateVersionResult> TakeAsync(
        string templateId,
        string version,
        string queue,
        CancellationToken cancellationToken) =>
        await _client!
            .ExecuteWorkflowAsync(
                (TakeVersionForTestWorkflow workflow) => workflow.RunAsync(
                    new TakeTemplateVersionInput(templateId, version, ByNameFixtures.Owner, "job-1")),
                new WorkflowOptions($"take-{Guid.NewGuid():N}", queue))
            .WaitAsync(cancellationToken);

    private async Task<ResolveTemplateVersionResult> ResolveAsync(
        string templateId,
        string consumer,
        CancellationToken cancellationToken)
    {
        await LongWorkerAsync(cancellationToken);
        return await ResolveAsync(templateId, consumer, TestQueue, cancellationToken);
    }

    private async Task<ResolveTemplateVersionResult> ResolveAsync(
        string templateId,
        string consumer,
        string queue,
        CancellationToken cancellationToken) =>
        await _client!
            .ExecuteWorkflowAsync(
                (ResolveVersionForTestWorkflow workflow) => workflow.RunAsync(
                    new ResolveTemplateVersionInput(templateId, consumer, "job-1")),
                new WorkflowOptions($"resolve-{Guid.NewGuid():N}", queue))
            .WaitAsync(cancellationToken);

    private WebApplicationFactory<PdfWorker> Worker(string resolveDeadline) =>
        new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Repository", "report-service/templates");
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Branch", "main");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IObjectStore>();
                services.AddSingleton<IObjectStore>(_sourceStore!);
            });
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:ResolveDeadline", resolveDeadline);
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private static async Task ReadyAsync(WebApplicationFactory<PdfWorker> worker, CancellationToken cancellationToken)
    {
        using var client = worker.CreateClient();
        await Wait.UntilAsync(
            $"/health answering {(int)HttpStatusCode.OK}",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            Deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
    }

    /// <summary>The class's own worker: the four-minute bound the other cases resolve under,
    /// started the first time a case needs it so a case that must resolve under another
    /// bound never has a second worker polling the queue.</summary>
    private async Task<WebApplicationFactory<PdfWorker>> LongWorkerAsync(CancellationToken cancellationToken)
    {
        if (_worker is { } running)
        {
            return running;
        }

        var worker = Worker(ResolutionDeadline);
        _worker = worker;
        await ReadyAsync(worker, cancellationToken);
        return worker;
    }

    /// <summary>The worker the deadline case resolves under: the shortest bound the setting allows.</summary>
    private async Task<ShortWorker> ShortWorkerAsync(CancellationToken cancellationToken)
    {
        var worker = Worker(ShortResolutionDeadline);
        await ReadyAsync(worker, cancellationToken);
        var testWorker = new TemporalWorker(
            _client!,
            new TemporalWorkerOptions(DeadlineQueue).AddWorkflow<ResolveVersionForTestWorkflow>().AddWorkflow<TakeVersionForTestWorkflow>());
        var stop = new CancellationTokenSource();
        var run = testWorker.ExecuteAsync(stop.Token);
        return new ShortWorker(worker, testWorker, stop, run, DeadlineQueue);
    }

    private async Task SeedAsync(params string[] versions)
    {
        foreach (var version in versions)
        {
            await _source!.PublishAsync(Template, version, Package(Template, version));
        }
    }

    private async Task SeedInvalidAsync(params string[] versions)
    {
        foreach (var version in versions)
        {
            await _source!.PublishAsync(ByNameFixtures.InvalidTemplate, version, ByNameFixtures.InvalidPackage(ByNameFixtures.InvalidTemplate, version));
        }
    }

    private async Task SeedUnsupportedAsync(params string[] versions)
    {
        foreach (var version in versions)
        {
            await _source!.PublishAsync(ByNameFixtures.UnsupportedTemplate, version, ByNameFixtures.UnsupportedPackage(ByNameFixtures.UnsupportedTemplate, version));
        }
    }

    private async Task SeedPreReleasesAsync(params string[] versions)
    {
        foreach (var version in versions.Length == 0 ? ["2.0.0-beta.1", "2.0.0-rc.1"] : versions)
        {
            await _source!.PublishAsync(ByNameFixtures.PreReleaseTemplate, version, ByNameFixtures.Valid(ByNameFixtures.PreReleaseTemplate, version));
        }
    }

    private async Task SeedEqualPrecedenceAsync(params string[] versions)
    {
        foreach (var version in versions.Length == 0 ? ByNameFixtures.EqualPrecedence : versions)
        {
            await _source!.PublishAsync(ByNameFixtures.EqualPrecedenceTemplate, version, ByNameFixtures.Valid(ByNameFixtures.EqualPrecedenceTemplate, version));
        }
    }

    private async Task SeedForeignAsync(string foreignVersion = ByNameFixtures.Second, string sharedVersion = ByNameFixtures.Shared)
    {
        await _source!.PublishAsync(ByNameFixtures.ForeignTemplate, foreignVersion, ByNameFixtures.Valid(ByNameFixtures.ForeignTemplate, foreignVersion, ByNameFixtures.Other, shared: false));
        await _source!.PublishAsync(ByNameFixtures.ForeignTemplate, sharedVersion, ByNameFixtures.Valid(ByNameFixtures.ForeignTemplate, sharedVersion, ByNameFixtures.Other, shared: true));
    }

    private Task SeedEmptyAsync() => Task.CompletedTask;

    private static Dictionary<string, byte[]> Package(string templateId, string version) => version switch
    {
        ByNameFixtures.Invalid => ByNameFixtures.InvalidPackage(templateId, version),
        ByNameFixtures.Unsupported => ByNameFixtures.UnsupportedPackage(templateId, version),
        ByNameFixtures.NotAVersion => ByNameFixtures.Valid(templateId, "1.3.0"),
        _ => ByNameFixtures.Valid(templateId, version),
    };

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private async Task<StoredVersion> RowAsync(string templateId, string version, CancellationToken cancellationToken) =>
        await RowOrNullAsync(templateId, version, cancellationToken)
        ?? throw new Xunit.Sdk.XunitException($"no record was written for {templateId} {version}");

    private async Task<StoredVersion?> RowOrNullAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<StoredVersion>(new CommandDefinition(
            """
            SELECT template_id AS "TemplateId", version AS "Version", owner_consumer_id AS "OwnerConsumerId",
                   engine AS "Engine", source_descriptor_sha256 AS "SourceDescriptorSha256", package_sha256 AS "PackageSha256",
                   object_key AS "ObjectKey"
              FROM report_service.template_versions
             WHERE template_id = @TemplateId AND version = @Version
            """,
            new { TemplateId = templateId, Version = version },
            cancellationToken: cancellationToken));
    }

    /// <summary>A store whose source read can be made to fail, as an outage does, and answer again.</summary>
    private sealed class SwitchableStore(IObjectStore inner) : IObjectStore
    {
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
    /// <summary>One record as the table holds it.</summary>
    private sealed class StoredVersion
    {
        public string TemplateId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string OwnerConsumerId { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string? SourceDescriptorSha256 { get; set; }

        public string PackageSha256 { get; set; } = string.Empty;

        public string ObjectKey { get; set; } = string.Empty;
    }

    /// <summary>A worker started for one case, with its own queue and its own bound.</summary>
    private sealed class ShortWorker(
        WebApplicationFactory<PdfWorker> worker,
        TemporalWorker testWorker,
        CancellationTokenSource stop,
        Task run,
        string queue) : IAsyncDisposable
    {
        public string Queue { get; } = queue;

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The worker was asked to stop, which is how a run of one ends.
            }

            testWorker.Dispose();
            stop.Dispose();
            await worker.DisposeAsync();
        }
    }
}

/// <summary>
/// The test's own caller: one workflow that asks the PDF rendering queue for one
/// by-name resolution with the execution settings the product gives that caller.
/// It carries no product behaviour of its own — it exists so the activity runs
/// exactly as a job's step will schedule it.
/// </summary>
[Workflow("ResolveVersionForTest")]
public sealed class ResolveVersionForTestWorkflow
{
    /// <summary>Asks for one by-name resolution on the PDF rendering queue.</summary>
    /// <param name="input">The template and the consumer that asks for it.</param>
    [WorkflowRun]
    public Task<ResolveTemplateVersionResult> RunAsync(ResolveTemplateVersionInput input) =>
        Workflow.ExecuteActivityAsync(
            (ResolveTemplateVersionActivity activity) => activity.RunAsync(input),
            TemplateResolutionActivityOptions.Create());
}


