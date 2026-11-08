extern alias pdf;

using System.Globalization;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
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
/// share, and the template source read from the deployment's own class on that
/// root. The worker is hosted on its own registrations, so what runs is the
/// registration the process ships; a test workflow on a queue of this process calls
/// <c>TakeTemplateVersion</c> on <c>report-pdf-rendering</c> with the execution
/// settings its caller is given.
/// <para>
/// Every package this class <b>takes</b> is a package the product's own rules
/// accept: the checks that refuse an invalid one ship later, and a taken copy is
/// never re-checked, so the refused packages are never taken - they are refused
/// before anything is written.
/// </para>
/// <para>
/// The two-workflow interleaving of the conflict (a take paused after its read) and
/// the failed commit of AC-6 are not arranged here: the class read has no pause over
/// the store, and both paths are proved hermetically on the same service. The cycle
/// re-runs this class on the approved commit.
/// </para>
/// </summary>
public sealed class TemplateTakeWorkerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Owner = "billing-system";
    private const string Other = "reporting-portal";
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";
    private const string TestQueue = "template-take-tests";

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-take-worker-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

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
    public TemplateTakeWorkerTests(PostgresFixture fixture) => _fixture = fixture;

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

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-template-take", cancellationToken);
        _hostPort = _engine.HostPort;

        _source = await TemplateSourceClass.CreateAsync(StorageRoot());
        _sourceStore = new SwitchableStore(_source.Store);

        _client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" }).WaitAsync(cancellationToken);

        _worker = new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
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
    public async Task AFirstTakeRecordsTheVersionKeepsTheObjectAndAnswersItsValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync(Version, TemplatePackageFixtures.Valid(Template, Version, Owner));

        var result = await TakeAsync(Template, Version, Owner, cancellationToken);

        Assert.Equal(TakeTemplateVersionOutcomes.Taken, result.Outcome);
        Assert.Null(result.CommitSha);
        Assert.Equal(64, result.SourceDescriptorSha256!.Length);
        Assert.NotNull(result.SourceObjects);

        var row = await RowAsync(Template, Version, cancellationToken);
        Assert.Equal(Owner, row.OwnerConsumerId);
        Assert.Equal("SCRIBAN", row.Engine);
        Assert.Null(row.CommitSha);
        Assert.Equal(result.SourceDescriptorSha256, row.SourceDescriptorSha256);
        Assert.Equal(result.PackageSha256, row.PackageSha256);
        Assert.Equal($"templates/{Owner}/{Template}/{Version}/{result.PackageSha256}", row.ObjectKey);
        Assert.True(File.Exists(Path.Combine(StorageRoot(), row.ObjectKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ATakenVersionIsAnsweredAgainWithoutReadingTheSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync(Version, TemplatePackageFixtures.Valid(Template, Version, Owner));
        await TakeAsync(Template, Version, Owner, cancellationToken);

        // The source is gone: a taken version is answered from its record and never read.
        await _source!.RemoveAsync(Template, Version);
        var again = await TakeAsync(Template, Version, Owner, cancellationToken);

        Assert.Equal(TakeTemplateVersionOutcomes.AlreadyTaken, again.Outcome);
    }

    [Fact]
    public async Task ATakenVersionIsAnsweredAgainWithTheSourceUnreadable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync(Version, TemplatePackageFixtures.Valid(Template, Version, Owner));
        await TakeAsync(Template, Version, Owner, cancellationToken);

        // The class cannot be listed or read: the record answers and the source is not reached.
        _sourceStore!.Fail = true;
        var again = await TakeAsync(Template, Version, Owner, cancellationToken);
        _sourceStore.Fail = false;

        Assert.Equal(TakeTemplateVersionOutcomes.AlreadyTaken, again.Outcome);
        Assert.Equal(Version, again.Version);
    }

    [Fact]
    public async Task AVersionNotTakenWithTheSourceDownEndsWithTheRetryableCodeAndWritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync("1.4.0", TemplatePackageFixtures.Valid(Template, "1.4.0", Owner));
        _sourceStore!.Fail = true;

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.4.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateSourceUnavailable, cause.ErrorType);
        Assert.False(cause.NonRetryable);
        Assert.Null(await RowOrNullAsync(Template, "1.4.0", cancellationToken));
    }

    [Fact]
    public async Task FilesWithoutARecordEndWithTemplateInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _source!.WriteFileAsync(Template, "1.5.0", "template.yaml", System.Text.Encoding.UTF8.GetBytes("manifestVersion: \"1.0\""));

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.5.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, cause.ErrorType);
        Assert.True(cause.NonRetryable);
        Assert.Contains("Nothing was published", cause.Message, StringComparison.Ordinal);
        Assert.Null(await RowOrNullAsync(Template, "1.5.0", cancellationToken));
    }

    [Fact]
    public async Task AVersionWithNoPublicationEndsWithTemplateNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.6.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, cause.ErrorType);
        Assert.True(cause.NonRetryable);
    }

    [Fact]
    public async Task AFolderThatIsNotTheVersionsNameEndsWithTemplateInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync("1.2.0", TemplatePackageFixtures.FolderDoesNotMatchTheVersion(Template, "1.2.0", "1.2.1"));

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
        await PublishAsync("1.3.0", TemplatePackageFixtures.UnsupportedEngine(Template, "1.3.0"));

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.3.0", Other, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, cause.ErrorType);
        Assert.True(cause.NonRetryable);
        Assert.DoesNotContain("razor", cause.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await RowOrNullAsync(Template, "1.3.0", cancellationToken));
    }

    [Fact]
    public async Task ARecordThisBuildDoesNotReadEndsWithUnsupportedCapability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _source!.WriteRecordAsync(
            Template,
            "1.7.0",
            System.Text.Encoding.UTF8.GetBytes(
                """{"descriptorVersion":"2.0","templateId":"invoice-energy","version":"1.7.0","files":[],"fileCount":0,"totalSize":0}"""));

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.7.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.UnsupportedCapability, cause.ErrorType);
        Assert.True(cause.NonRetryable);
        Assert.Null(await RowOrNullAsync(Template, "1.7.0", cancellationToken));
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

    private Task PublishAsync(string version, IReadOnlyDictionary<string, byte[]> files) =>
        _source!.PublishAsync(Template, version, files);

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
                   engine AS "Engine", commit_sha AS "CommitSha", source_descriptor_sha256 AS "SourceDescriptorSha256",
                   package_sha256 AS "PackageSha256", object_key AS "ObjectKey"
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

        public string? CommitSha { get; set; }

        public string? SourceDescriptorSha256 { get; set; }

        public string PackageSha256 { get; set; } = string.Empty;

        public string ObjectKey { get; set; } = string.Empty;
    }
}

/// <summary>
/// The test's own caller: one workflow that asks the queue for one take with the
/// execution settings the product gives that caller. It carries no product
/// behaviour of its own - it exists so the activity runs exactly as its caller
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
