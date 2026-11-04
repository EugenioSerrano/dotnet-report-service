extern alias pdf;

using System.Globalization;
using System.Net;
using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ReportService.TemplateSource;
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
/// The deadline case runs a second worker on a five-second resolution bound, which
/// is the shortest the setting allows: its family holds enough refused versions
/// that the walk cannot finish inside it. The case is the one arrangement here that
/// depends on how long a real walk costs; it asserts that reaching the bound is
/// never reported as invalidity, and a run that finishes first fails by name rather
/// than passing with nothing checked.
/// </para>
/// </summary>
public sealed class TemplateByNameResolutionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int GrpcPort = 7233;
    private const string Template = ByNameFixtures.Template;
    private const string TestQueue = "template-by-name-tests";
    private const string DeadlineQueue = "template-by-name-deadline-tests";
    private const string ResolutionDeadline = "00:04:00";
    private const string ShortResolutionDeadline = "00:00:05";

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly int _enginePort = FreePort();
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-by-name-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    private readonly LogCapture _logs = new();

    private IContainer? _engine;
    private string _connectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private TemplateSourceHost? _stub;
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
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);

        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = _fixture.Application(database);
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = ContainerSmoke.Builder(_engineImage, "temporal-template-by-name")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_enginePort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _engineImage, cancellationToken);

        var stub = new GitHubTemplateSourceStub();
        _stub = await TemplateSourceHost.StartAsync(stub, cancellationToken);

        _client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_enginePort}") { Namespace = "default" }).WaitAsync(cancellationToken);

        _worker = Worker(ResolutionDeadline);
        await ReadyAsync(_worker, cancellationToken);

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
            await _testWorkerRun.ConfigureAwait(false);
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
            await _engine.DisposeAsync();
        }

        if (_stub is not null)
        {
            await _stub.DisposeAsync();
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
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First, ByNameFixtures.Second);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ResolveTemplateVersionOutcomes.Resolved, result.Outcome);
        Assert.Equal(ByNameFixtures.Second, result.Version);
        var row = await RowAsync(Template, ByNameFixtures.Second, cancellationToken);
        Assert.Equal(result.CommitSha, row.CommitSha);
        Assert.Equal(result.PackageSha256, row.PackageSha256);
    }

    [Fact]
    public async Task ANewerInvalidVersionIsSkippedAndTheOlderTakenOneUsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First, ByNameFixtures.Invalid);
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
        ByNameFixtures.SeedInvalid(_stub!.Stub, ByNameFixtures.First, ByNameFixtures.Second);

        var cause = await AssertRefusedAsync(ByNameFixtures.InvalidTemplate, TemplateResolutionCodes.TemplateInvalid, cancellationToken);

        Assert.True(cause.NonRetryable);
        Assert.Contains(ByNameFixtures.Second, cause.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ByNameFixtures.First, cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATruncatedTemplateFolderEndsWithTemplateInvalidAndTakesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First);
        _stub.Stub.SetTemplateFolderTruncated(Template);

        var cause = await AssertRefusedAsync(Template, TemplateResolutionCodes.TemplateInvalid, cancellationToken);

        Assert.True(cause.NonRetryable);
        Assert.Contains("too many entries", cause.Message, StringComparison.Ordinal);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.First, cancellationToken));
    }

    [Fact]
    public async Task APreReleaseIsNeverChosenByName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First, ByNameFixtures.Second, ByNameFixtures.PreRelease);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.Second, result.Version);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.PreRelease, cancellationToken));
    }

    [Fact]
    public async Task ATemplateWithOnlyPreReleasesEndsWithTemplateInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.SeedPreReleases(_stub!.Stub);

        var cause = await AssertRefusedAsync(ByNameFixtures.PreReleaseTemplate, TemplateResolutionCodes.TemplateInvalid, cancellationToken);

        Assert.Contains("has no release version", cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSourceDownUsesTheLatestReleaseAlreadyTaken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);
        _stub.Stub.Stop();

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
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.Unsupported, ByNameFixtures.HighestRelease);
        _stub.Stub.Stop();

        var cause = await AssertRefusedAsync(Template, TemplateResolutionCodes.TemplateSourceUnavailable, cancellationToken);

        Assert.False(cause.NonRetryable);
        Assert.Equal(TemplateVersionResolver.SourceUnavailableReason, cause.Message);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.HighestRelease, cancellationToken));
    }

    [Fact]
    public async Task TheOrderIsPrecedenceAndNotText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _stub!.Stub.SeedPackage(Template, ByNameFixtures.Invalid, ByNameFixtures.Valid(Template, ByNameFixtures.Invalid));
        _stub.Stub.SeedPackage(Template, ByNameFixtures.HighestRelease, ByNameFixtures.Valid(Template, ByNameFixtures.HighestRelease));

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        // "1.2.0" is the higher string; 1.10.0 is the higher version.
        Assert.Equal(ByNameFixtures.HighestRelease, result.Version);
        Assert.Null(await RowOrNullAsync(Template, ByNameFixtures.Invalid, cancellationToken));
    }

    [Fact]
    public async Task TheHigherFullVersionWinsWhenNeitherIsTaken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.SeedEqualPrecedence(_stub!.Stub);

        var result = await ResolveAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.EqualPrecedence[1], result.Version);
        Assert.Null(await RowOrNullAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.EqualPrecedence[0], cancellationToken));
    }

    [Fact]
    public async Task TheVersionAlreadyTakenWinsOnEqualPrecedence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.SeedEqualPrecedence(_stub!.Stub);
        await TakeAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.EqualPrecedence[0], cancellationToken);

        var result = await ResolveAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.EqualPrecedence[0], result.Version);
        Assert.Null(await RowOrNullAsync(ByNameFixtures.EqualPrecedenceTemplate, ByNameFixtures.EqualPrecedence[1], cancellationToken));
    }

    [Fact]
    public async Task AVersionAddedBetweenTwoResolutionsIsSeenOnlyByTheSecond()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First);

        var first = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);
        _stub.Stub.SeedPackage(Template, ByNameFixtures.Second, ByNameFixtures.Valid(Template, ByNameFixtures.Second));
        var second = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.First, first.Version);
        Assert.Equal(ByNameFixtures.Second, second.Version);
    }

    [Fact]
    public async Task ATakenVersionRemovedFromTheBranchIsStillUsed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First, ByNameFixtures.Second);
        await TakeAsync(Template, ByNameFixtures.Second, cancellationToken);
        _stub.Stub.RemoveVersion(Template, ByNameFixtures.Second);

        var result = await ResolveAsync(Template, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.Second, result.Version);
        Assert.NotNull(await RowOrNullAsync(Template, ByNameFixtures.Second, cancellationToken));
    }

    [Fact]
    public async Task AnotherConsumersNonSharedVersionIsExcludedAndASharedOneIsUsable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.SeedForeign(_stub!.Stub);

        var result = await ResolveAsync(ByNameFixtures.ForeignTemplate, ByNameFixtures.Owner, cancellationToken);

        Assert.Equal(ByNameFixtures.Shared, result.Version);
        Assert.Null(await RowOrNullAsync(ByNameFixtures.ForeignTemplate, ByNameFixtures.Second, cancellationToken));
    }

    [Fact]
    public async Task OnlyAnotherConsumersVersionAnswersTheConstantNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _stub!.Stub.SeedPackage(
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
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First, ByNameFixtures.Unsupported);
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
        ByNameFixtures.SeedUnsupported(_stub!.Stub, ByNameFixtures.First, ByNameFixtures.Second);

        var cause = await AssertRefusedAsync(ByNameFixtures.UnsupportedTemplate, TemplateResolutionCodes.TemplateInvalid, cancellationToken);

        Assert.Contains(ByNameFixtures.Second, cause.Message, StringComparison.Ordinal);
        Assert.Contains("not one this deployment renders", cause.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeadlineReachedBeforeADecisionIsNeverReportedAsInvalidity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        ByNameFixtures.Seed(_stub!.Stub, ByNameFixtures.First);
        await TakeAsync(Template, ByNameFixtures.First, cancellationToken);
        for (var index = 0; index < 400; index++)
        {
            var version = $"1.1.{index}";
            _stub.Stub.SeedPackage(Template, version, ByNameFixtures.InvalidPackage(Template, version));
        }

        await using var shortWorker = await ShortWorkerAsync(cancellationToken);
        var result = await ResolveAsync(Template, ByNameFixtures.Owner, shortWorker.Queue, cancellationToken);

        Assert.Equal(ResolveTemplateVersionOutcomes.ResolvedFromTakenCopy, result.Outcome);
        Assert.Equal(ByNameFixtures.First, result.Version);
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
        CancellationToken cancellationToken) =>
        await _client!
            .ExecuteWorkflowAsync(
                (TakeVersionForTestWorkflow workflow) => workflow.RunAsync(
                    new TakeTemplateVersionInput(templateId, version, ByNameFixtures.Owner, "job-1")),
                new WorkflowOptions($"take-{Guid.NewGuid():N}", TestQueue))
            .WaitAsync(cancellationToken);

    private Task<ResolveTemplateVersionResult> ResolveAsync(
        string templateId,
        string consumer,
        CancellationToken cancellationToken) =>
        ResolveAsync(templateId, consumer, TestQueue, cancellationToken);

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
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_enginePort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:ApiBaseUrl", _stub!.BaseUrl.AbsoluteUri);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Repository", TemplateSourceOptionsRepository());
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Branch", GitHubTemplateSourceStub.Branch);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:AllowInsecureLoopback", "true");
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

    /// <summary>A second worker whose resolution bound is the shortest the setting allows.</summary>
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

    private static string TemplateSourceOptionsRepository() =>
        GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository;

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
                   engine AS "Engine", commit_sha AS "CommitSha", package_sha256 AS "PackageSha256",
                   object_key AS "ObjectKey"
              FROM report_service.template_versions
             WHERE template_id = @TemplateId AND version = @Version
            """,
            new { TemplateId = templateId, Version = version },
            cancellationToken: cancellationToken));
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>One record as the table holds it.</summary>
    private sealed class StoredVersion
    {
        public string TemplateId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string OwnerConsumerId { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string CommitSha { get; set; } = string.Empty;

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
            await run.ConfigureAwait(false);
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
