extern alias pdf;

using System.Globalization;
using System.Net;
using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.TemplateSource.Take;
using pdf::ReportService.Workers.Pdf.Templates;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The take on its queue, with everything real around it: PostgreSQL 18.6 with the
/// product's migrations, a real engine, a storage root the worker and this process
/// share, and the template source served over real HTTP on a loopback port. The
/// worker is hosted on its own registrations, so what runs is the registration the
/// process ships; a test workflow on a queue of this process calls
/// <c>TakeTemplateVersion</c> on <c>report-pdf-rendering</c> with the execution
/// settings its caller is given.
/// <para>
/// Every package this class <b>takes</b> is a package the product's own rules
/// accept: the checks that refuse an invalid one ship later, and a taken copy is
/// never re-checked, so the refused packages are never taken — they are refused
/// before anything is written.
/// </para>
/// <para>
/// The two-workflow interleaving of the conflict (a take paused after its read) and
/// the failed commit of AC-6 are not arranged here: the source double has no pause
/// over a socket, and both paths are proved hermetically on the same service. The
/// cycle re-runs this class on the approved commit.
/// </para>
/// </summary>
public sealed class TemplateTakeWorkerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int GrpcPort = 7233;
    private const string Owner = "billing-system";
    private const string Other = "reporting-portal";
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";
    private const string TestQueue = "template-take-tests";

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly int _enginePort = FreePort();
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-take-worker-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

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
    public TemplateTakeWorkerTests(PostgresFixture fixture) => _fixture = fixture;

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

        _engine = ContainerSmoke.Builder(_engineImage, "temporal-template-take")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_enginePort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _engineImage, cancellationToken);

        var stub = new GitHubTemplateSourceStub();
        stub.SeedPackage(Template, Version, TemplatePackageFixtures.Valid(Template, Version, Owner));
        stub.SeedPackage(Template, "1.2.0", TemplatePackageFixtures.FolderDoesNotMatchTheVersion(Template, "1.2.0", "1.2.1"));
        stub.SeedPackage(Template, "1.3.0", TemplatePackageFixtures.UnsupportedEngine(Template, "1.3.0"));
        stub.SeedPackage(Template, "1.4.0", TemplatePackageFixtures.Valid(Template, "1.4.0", Owner));
        _stub = await TemplateSourceHost.StartAsync(stub, cancellationToken);

        _client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_enginePort}") { Namespace = "default" }).WaitAsync(cancellationToken);

        _worker = new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_enginePort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:ApiBaseUrl", _stub.BaseUrl.AbsoluteUri);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Repository", TemplateSourceOptionsRepository());
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Branch", GitHubTemplateSourceStub.Branch);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:AllowInsecureLoopback", "true");
        });
        using var health = _worker.CreateClient();
        await Wait.UntilAsync(
            $"/health answering {(int)HttpStatusCode.OK}",
            async token =>
            {
                using var response = await health.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            Deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        _testWorker = new TemporalWorker(
            _client,
            new TemporalWorkerOptions(TestQueue).AddWorkflow<TakeVersionForTestWorkflow>());
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
    public async Task AFirstTakeRecordsTheVersionKeepsTheObjectAndAnswersItsValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await TakeAsync(Template, Version, Owner, cancellationToken);

        Assert.Equal(TakeTemplateVersionOutcomes.Taken, result.Outcome);
        var row = await RowAsync(Template, Version, cancellationToken);
        Assert.Equal(Owner, row.OwnerConsumerId);
        Assert.Equal("SCRIBAN", row.Engine);
        Assert.Equal(result.CommitSha, row.CommitSha);
        Assert.Equal(result.PackageSha256, row.PackageSha256);
        Assert.Equal($"templates/{Owner}/{Template}/{Version}/{result.PackageSha256}", row.ObjectKey);
        Assert.True(File.Exists(Path.Combine(StorageRoot(), row.ObjectKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ATakenVersionIsAnsweredAgainWithoutOneRequestToTheSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await TakeAsync(Template, Version, Owner, cancellationToken);
        _stub!.Stub.Stop();
        var requests = _stub.Stub.RequestCount;

        var again = await TakeAsync(Template, Version, Owner, cancellationToken);

        Assert.Equal(TakeTemplateVersionOutcomes.AlreadyTaken, again.Outcome);
        Assert.Equal(requests, _stub.Stub.RequestCount);
    }

    [Fact]
    public async Task AVersionNotTakenWithTheSourceDownEndsWithTheRetryableCodeAndWritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _stub!.Stub.Stop();

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.4.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateSourceUnavailable, cause.ErrorType);
        Assert.False(cause.NonRetryable);
        Assert.Null(await RowOrNullAsync(Template, "1.4.0", cancellationToken));
    }

    [Fact]
    public async Task AFolderThatIsNotTheVersionsNameEndsWithTemplateInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.2.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, cause.ErrorType);
        Assert.True(cause.NonRetryable);
        Assert.Contains("1.2.1", cause.Message, StringComparison.Ordinal);
        Assert.Null(await RowOrNullAsync(Template, "1.2.0", cancellationToken));
    }

    [Fact]
    public async Task AnotherConsumersUntakenVersionIsAnsweredAsNotFoundBeforeAnyContentRefusal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.3.0", Other, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, cause.ErrorType);
        Assert.True(cause.NonRetryable);
        Assert.DoesNotContain("razor", cause.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await RowOrNullAsync(Template, "1.3.0", cancellationToken));
    }

    /// <summary>The codes the class asserts on, which the take defines.</summary>
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

    private static string TemplateSourceOptionsRepository() =>
        GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository;

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private async Task<TakeTemplateVersionResult> TakeAsync(
        string templateId,
        string version,
        string consumer,
        CancellationToken cancellationToken) =>
        await _client!
            .ExecuteWorkflowAsync(
                (TakeVersionForTestWorkflow workflow) => workflow.RunAsync(
                    new TakeTemplateVersionInput(templateId, version, consumer, "job-1")),
                new WorkflowOptions($"take-{Guid.NewGuid():N}", TestQueue))
            .WaitAsync(cancellationToken);

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
}

/// <summary>
/// The test's own caller: one workflow that asks the queue for one take with the
/// execution settings the product gives that caller. It carries no product
/// behaviour of its own — it exists so the activity runs exactly as its caller
/// will schedule it.
/// </summary>
[Workflow(TakeTemplateVersionActivityOptions.Queue)]
public sealed class TakeVersionForTestWorkflow
{
    /// <summary>Asks for one take on the PDF rendering queue.</summary>
    /// <param name="input">The version to take and who asks for it.</param>
    [WorkflowRun]
    public Task<TakeTemplateVersionResult> RunAsync(TakeTemplateVersionInput input) =>
        Workflow.ExecuteActivityAsync(
            (TakeTemplateVersionActivity activity) => activity.RunAsync(input),
            TakeTemplateVersionActivityOptions.Create());
}
