extern alias pdf;

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ReportService.IntegrationTests.Jobs;
using ReportService.IntegrationTests.Templates;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateChecks;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;
using pdf::ReportService.Workers.Pdf.Templates;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests;

/// <summary>
/// The at-bound gate on the take's queue, with everything real around it: PostgreSQL 18.6
/// with the product's migrations, a real engine, a storage root the worker and this process
/// share, the template source read from the deployment's own class on that root, and the PDF
/// worker's own registrations - the twelfth check, the thirteenth check and the renderer the
/// process ships. A test workflow on a queue of this process calls <c>TakeTemplateVersion</c>
/// on <c>report-pdf-rendering</c> with its own execution settings.
/// <para>
/// The control version's sample, empty sample and at-bound sample all print and verify, and
/// it is taken with its record, its object and the object's SHA-256 read back. A version whose
/// row part clips the at-bound cell is refused <c>TEMPLATE_INVALID</c> naming the part and the
/// position, with no record and no object. A version whose at-bound render is held past its
/// budget is retried inside the activity's three attempts and never refused. The repeat of a
/// taken version answers <c>ALREADY_TAKEN</c>. A version whose sample carries no detail record
/// is taken after its at-bound record is synthesised from the contract. The worker is hosted
/// in this process so the held-render case can answer through the port exactly as the product
/// does when a budget trips; the class summary of <c>AtBoundSampleRenderingTests</c> carries
/// the printed evidence on the PDF base image.
/// </para>
/// </summary>
public sealed class AtBoundSampleTakeTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Owner = AtBoundFixtures.Owner;

    private const string Template = AtBoundFixtures.TemplateId;

    private const string TestQueue = "at-bound-take-tests";

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-at-bound-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;
    private int _hostPort;
    private string _connectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private TemplateSourceClass? _source;
    private WebApplicationFactory<PdfWorker>? _worker;
    private HoldingSampleRenderer? _holder;
    private TemporalWorker? _testWorker;
    private readonly CancellationTokenSource _testWorkerStop = new();
    private Task? _testWorkerRun;
    private TemporalClient? _client;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public AtBoundSampleTakeTests(PostgresFixture fixture) => _fixture = fixture;

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

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-at-bound", cancellationToken);
        _hostPort = _engine.HostPort;

        _source = await TemplateSourceClass.CreateAsync(SourceRoot());

        _client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" }).WaitAsync(cancellationToken);

        _worker = new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting("TemplateSource:Store:Filesystem:Root", SourceRoot());
            builder.UseSetting("TemplateSource:Store:Filesystem:CreateIfMissing", "false");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.ConfigureTestServices(services =>
            {
                // The one seam this class installs: the sample port's own answer, so the
                // held-render case meets the state a budget trip produces without waiting one
                // out. The renderer's behaviour is unchanged when the seam is off.
                services.RemoveAll<ISampleRenderer>();
                services.AddSingleton<ISampleRenderer>(provider =>
                {
                    var inner = ActivatorUtilities.CreateInstance<pdf::ReportService.Workers.Pdf.Templates.PdfSampleRenderer>(provider);
                    _holder = new HoldingSampleRenderer(inner);
                    return _holder;
                });
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

        // The port is resolved once here so the seam is in hand before any case asks for it:
        // the container built the renderer from the worker's own registrations.
        _holder = Assert.IsType<HoldingSampleRenderer>(_worker.Services.GetRequiredService<ISampleRenderer>());
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
    public async Task TheControlVersionIsTakenWithItsRecordObjectAndHash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync("1.0.0", AtBoundFixtures.Package(), cancellationToken);

        var result = await TakeAsync("1.0.0", cancellationToken);

        Assert.Equal(TakeTemplateVersionOutcomes.Taken, result.Outcome);
        Assert.Equal(64, result.PackageSha256!.Length);
        var row = await RowAsync("1.0.0", cancellationToken);
        Assert.NotNull(row);
        Assert.Equal(Owner, row.OwnerConsumerId);
        Assert.Equal(result.PackageSha256, row.PackageSha256);

        var objectPath = Path.Combine(StorageRoot(), row.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(objectPath), $"the taken object is missing: {objectPath}");
        Assert.Equal(row.PackageSha256, Sha256Of(File.ReadAllBytes(objectPath)));
    }

    [Fact]
    public async Task AVersionWhoseRowPartClipsTheAtBoundCellIsRefusedAndNothingIsKept()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var package = AtBoundFixtures.Package();
        package["html/row.html.sbn"] = Encoding.UTF8.GetBytes(
            "<tr><td class=\"seq\">{{ item.sequence | html.escape }}</td>"
            + "<td class=\"cell\"><div class=\"clip\">{{ item.description | html.escape }}</div></td>"
            + "<td class=\"amount\">{{ item.amount | html.escape }}</td></tr>");
        package["html/styles.css"] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(package["html/styles.css"]) + " div.clip { height: 40mm; overflow: hidden; } ");
        await PublishAsync("1.0.1", package, cancellationToken);

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => TakeAsync("1.0.1", cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, cause.ErrorType);
        Assert.True(cause.NonRetryable);
        Assert.Contains("html/row.html.sbn", cause.Message, StringComparison.Ordinal);
        Assert.Contains("$.description", cause.Message, StringComparison.Ordinal);
        Assert.Contains("text-dropped", cause.Message, StringComparison.Ordinal);
        Assert.Null(await RowOrNullAsync("1.0.1", cancellationToken));
        AssertNoCopyWritten("1.0.1");
    }

    [Fact]
    public async Task AVersionWhoseAtBoundRenderIsHeldPastItsBudgetIsRetriedAndNeverRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync("1.0.2", AtBoundFixtures.Package(), cancellationToken);
        Assert.NotNull(_holder);
        _holder!.Hold = true;
        _holder.Held = 0;
        try
        {
            var failure = await Assert.ThrowsAsync<WorkflowFailedException>(() => TakeAsync("1.0.2", cancellationToken));

            var cause = ApplicationFailure(failure);
            Assert.Equal(TemplateTakeCodes.RenderTimeout, cause.ErrorType);
            Assert.False(cause.NonRetryable, "a render that did not finish is retryable, never a refusal.");

            // The engine retried inside the activity's three attempts: the at-bound render was
            // asked for again, and the version was never refused for it.
            Assert.True(_holder.Held >= 2, $"the at-bound render was asked for {_holder.Held} time(s).");
            Assert.Null(await RowOrNullAsync("1.0.2", cancellationToken));
            AssertNoCopyWritten("1.0.2");
        }
        finally
        {
            _holder!.Hold = false;
        }
    }

    [Fact]
    public async Task TheRepeatOfATakenVersionAnswersAlreadyTaken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync("1.0.3", AtBoundFixtures.Package(), cancellationToken);
        Assert.Equal(TakeTemplateVersionOutcomes.Taken, (await TakeAsync("1.0.3", cancellationToken)).Outcome);

        var again = await TakeAsync("1.0.3", cancellationToken);

        Assert.Equal(TakeTemplateVersionOutcomes.AlreadyTaken, again.Outcome);
        Assert.Equal("1.0.3", again.Version);
    }

    [Fact]
    public async Task AVersionWhoseSampleCarriesNoDetailRecordIsTakenWithItsSynthesisedRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PublishAsync("1.0.4", AtBoundFixtures.Package(records: []), cancellationToken);

        var result = await TakeAsync("1.0.4", cancellationToken);

        Assert.Equal(TakeTemplateVersionOutcomes.Taken, result.Outcome);
        var row = await RowAsync("1.0.4", cancellationToken);
        Assert.NotNull(row);
        var objectPath = Path.Combine(StorageRoot(), row.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(objectPath), $"the taken object is missing: {objectPath}");
        Assert.Equal(row.PackageSha256, Sha256Of(File.ReadAllBytes(objectPath)));
    }

    private async Task PublishAsync(string version, IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken)
    {
        var relabelled = new Dictionary<string, byte[]>(files, StringComparer.Ordinal)
        {
            ["template.yaml"] = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(files["template.yaml"])
                    .Replace("version: \"1.0.0\"", "version: \"" + version + "\"", StringComparison.Ordinal)),
        };
        await _source!.PublishAsync(Template, version, relabelled);
    }

    private async Task<TakeTemplateVersionResult> TakeAsync(string version, CancellationToken cancellationToken) =>
        await _client!
            .ExecuteWorkflowAsync(
                (TakeVersionForTestWorkflow workflow) => workflow.RunAsync(
                    new TakeTemplateVersionInput(Template, version, Owner, "job-1")),
                new WorkflowOptions($"take-{Guid.NewGuid():N}", TestQueue))
            .WaitAsync(cancellationToken);

    private async Task<StoredVersion?> RowOrNullAsync(string version, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<StoredVersion>(new CommandDefinition(
            """
            SELECT template_id AS "TemplateId", version AS "Version", owner_consumer_id AS "OwnerConsumerId",
                   engine AS "Engine", engine_version AS "EngineVersion", package_sha256 AS "PackageSha256",
                   object_key AS "ObjectKey", package_size AS "PackageSize"
              FROM report_service.template_versions
             WHERE template_id = @TemplateId AND version = @Version
            """,
            new { TemplateId = Template, Version = version },
            cancellationToken: cancellationToken));
    }

    private async Task<StoredVersion> RowAsync(string version, CancellationToken cancellationToken) =>
        await RowOrNullAsync(version, cancellationToken)
            ?? throw new Xunit.Sdk.XunitException($"{Template} {version} was not recorded as taken.");

    private void AssertNoCopyWritten(string version)
    {
        var folder = Path.Combine(StorageRoot(), "templates", Owner, Template, version);
        var written = Directory.Exists(folder)
            && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any();
        Assert.False(written, $"a copy was written for {Template} {version}");
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

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private string SourceRoot() => Path.Combine(_directory, "template-source");

    /// <summary>The port's own answer, with one switch: the at-bound request held past its budget.</summary>
    private sealed class HoldingSampleRenderer(ISampleRenderer inner) : ISampleRenderer
    {
        /// <summary>Whether the at-bound request is answered as a budget trip.</summary>
        public bool Hold { get; set; }

        /// <summary>How many at-bound requests were answered as held.</summary>
        public int Held { get; set; }

        /// <inheritdoc />
        public Task<SampleAttempt> RenderAsync(SampleRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (Hold && request.SampleJson.Length > 200_000)
            {
                Held++;
                return Task.FromResult<SampleAttempt>(
                    new SampleAttempt.Unavailable(SampleUnavailableCause.RenderTimeout, "html/row.html.sbn"));
            }

            return inner.RenderAsync(request, cancellationToken);
        }
    }

    private sealed class StoredVersion
    {
        public string TemplateId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string OwnerConsumerId { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string EngineVersion { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public long PackageSize { get; set; }

        public string ObjectKey { get; set; } = string.Empty;
    }
}

/// <summary>
/// The joint group with the version-taking story's job step: a job naming the version this
/// gate refuses is accepted, ends <c>failed</c> with <c>TEMPLATE_INVALID</c> and publishes
/// nothing, and a job naming the control ends <c>succeeded</c> with one artifact. The group
/// runs only on a commit that carries the job step which resolves a template, and names what
/// is missing when it does not - never silently.
/// </summary>
public sealed class AtBoundSampleJointGroupTests : IClassFixture<PostgresFixture>
{
    private const string Template = AtBoundFixtures.TemplateId;

    private const string Consumer = PrintingRig.SampleGateOwner;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public AtBoundSampleJointGroupTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AJobNamingTheRefusingVersionFailsAndOneNamingTheControlSucceeds()
    {
        var absent = JobStepAbsences();
        Assert.SkipUnless(
            absent.Count == 0,
            "the joint group runs only on a commit that carries the job step which resolves a template, and this "
            + "commit does not: " + string.Join("; ", absent) + ".");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);

        var clipping = AtBoundFixtures.Package();
        clipping["html/row.html.sbn"] = Encoding.UTF8.GetBytes(
            "<tr><td class=\"seq\">{{ item.sequence | html.escape }}</td>"
            + "<td class=\"cell\"><div class=\"clip\">{{ item.description | html.escape }}</div></td>"
            + "<td class=\"amount\">{{ item.amount | html.escape }}</td></tr>");
        clipping["html/styles.css"] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(clipping["html/styles.css"]) + " div.clip { height: 40mm; overflow: hidden; } ");
        await rig.Source.PublishAsync(Template, "1.0.0", Relabel(AtBoundFixtures.Package(), "1.0.0"));
        await rig.Source.PublishAsync(Template, "1.0.1", Relabel(clipping, "1.0.1"));

        // The job naming the refusing version: accepted, then failed with the take's code, and
        // nothing published.
        var refusedUpload = await rig.ReadyUploadAsync(Snapshot(), cancellationToken, Consumer);
        var refusedJob = await rig.SubmitAsync(refusedUpload, Template, "1.0.1", culture: null, timeZone: null, cancellationToken, Consumer);
        var refused = await rig.WaitForTerminalAsync(refusedJob, Deadline, cancellationToken);

        Assert.Equal("FAILED", refused.Status);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.ErrorCode);
        Assert.Null(refused.ArtifactId);
        Assert.Empty(rig.OutputObjectKeys(refusedJob, string.Empty));

        // The job naming the control: taken, printed and published as one artifact.
        var controlUpload = await rig.ReadyUploadAsync(Snapshot(), cancellationToken, Consumer);
        var controlJob = await rig.SubmitAsync(controlUpload, Template, "1.0.0", culture: null, timeZone: null, cancellationToken, Consumer);
        var control = await rig.WaitForTerminalAsync(controlJob, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", control.Status);
        Assert.NotNull(control.ArtifactId);
        var artifact = await rig.ReadArtifactAsync(control, cancellationToken);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(artifact, 0, 4));
        var artifacts = await CountArtifactsAsync(rig, controlJob, cancellationToken);
        Assert.Equal(1, artifacts);
    }

    /// <summary>The job's data, valid under the control's contract.</summary>
    private static byte[] Snapshot() =>
        Encoding.UTF8.GetBytes(
            """{ "contractVersion": "1.0", "header": { "title": "Monthly statement" }, "items": [ { "sequence": 1, "description": "Consulting", "amount": 100 } ] }""");

    private static Dictionary<string, byte[]> Relabel(IReadOnlyDictionary<string, byte[]> files, string version)
    {
        var copy = new Dictionary<string, byte[]>(files, StringComparer.Ordinal)
        {
            ["template.yaml"] = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(files["template.yaml"])
                    .Replace("version: \"1.0.0\"", "version: \"" + version + "\"", StringComparison.Ordinal)),
        };
        return copy;
    }

    private static async Task<long> CountArtifactsAsync(PrintingRig rig, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await rig.DataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT count(*)
              FROM report_service.artifacts a
              JOIN report_service.report_outputs o ON o.output_id = a.output_id
             WHERE o.report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    /// <summary>What a commit that carries the take but not the job step is missing, by name.</summary>
    private static IReadOnlyList<string> JobStepAbsences()
    {
        const string orchestration = "ReportService.Workers.Orchestration";
        const string workflowType = "ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow";
        var absent = new List<string>();
        Type? workflow;
        try
        {
            workflow = Assembly.Load(new AssemblyName(orchestration)).GetType(workflowType, throwOnError: false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            workflow = null;
        }

        if (workflow is null)
        {
            absent.Add($"no type '{workflowType}'");
            return absent;
        }

        if (workflow.GetMethod("ResolveTemplateAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is null)
        {
            absent.Add($"'{workflowType}' declares no 'ResolveTemplateAsync', the job step that resolves a template");
        }

        if (workflow.GetField("TemplateResolvePatch", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string != "template-resolve-v1")
        {
            absent.Add($"'{workflowType}.TemplateResolvePatch' is not 'template-resolve-v1'");
        }

        return absent;
    }
}
