extern alias pdf;

using System.Globalization;
using System.Net;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReportService.IntegrationTests.Templates;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;
using pdf::ReportService.Workers.Pdf.Templates;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests;

/// <summary>
/// The sample render on the take's own queue, with everything real around it: PostgreSQL
/// 18.6 with the product's migrations, a real engine, a storage root the worker and this
/// process share, the template source read from the deployment's own class on that root,
/// and the PDF worker's own registrations - the twelfth check and the renderer the process
/// ships included.
/// <para>
/// The packages this class takes are the <c>sample-gate</c> family of the test library: three
/// versions of one report, one engine each, each carrying its owner and its own
/// <c>sample.json</c>. Two of its cases are one mutation of that package - a row part that
/// prints no text, and a header-only sample whose document parts print nothing - and each
/// asserts the refusal's code and rule, no record in the store and no object under the copy's
/// key. The renderer-held-down case and the take's own deadline are proved hermetically on the
/// same service and are not arranged here.
/// </para>
/// </summary>
public sealed class TemplateSampleRenderTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Owner = "billing-system";
    private const string Template = "sample-gate";
    private const string TestQueue = "template-sample-render-tests";

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    private static readonly (string Version, string Engine, string EngineVersion)[] Versions =
    [
        ("1.0.0", "SCRIBAN", "7.5.1"),
        ("1.1.0", "HANDLEBARS", "2.4.3"),
        ("1.2.0", "LIQUID_FLUID", "2.40.0"),
    ];

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-sample-render-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;
    private int _hostPort;
    private string _connectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private TemplateSourceClass? _source;
    private WebApplicationFactory<PdfWorker>? _worker;
    private TemporalWorker? _testWorker;
    private readonly CancellationTokenSource _testWorkerStop = new();
    private Task? _testWorkerRun;
    private TemporalClient? _client;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public TemplateSampleRenderTests(PostgresFixture fixture) => _fixture = fixture;

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

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-sample-render", cancellationToken);
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
    public async Task TheThreeSampleGateVersionsAreTakenEachWithItsOwnAdaptersVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var (version, _, engineVersion) in Versions)
        {
            await PublishAsync(version, SampleGate(version), cancellationToken);

            var result = await TakeAsync(Template, version, Owner, cancellationToken);

            Assert.Equal(TakeTemplateVersionOutcomes.Taken, result.Outcome);
            var row = await RowOrNullAsync(Template, version, cancellationToken);
            Assert.NotNull(row);
            Assert.Equal(engineVersion, row.EngineVersion);
            Assert.Equal(Owner, row.OwnerConsumerId);
        }
    }

    [Fact]
    public async Task ARowThatPrintsNoTextIsRefusedWithRowWithoutTextAndNothingIsKept()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var package = SampleGate("1.0.0");
        RelabelVersion(package, "1.0.0", "1.3.0");
        package["html/row.html.sbn"] = "<tr><td></td></tr>"u8.ToArray();
        await PublishAsync("1.3.0", package, cancellationToken);

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.3.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, cause.ErrorType);
        Assert.Contains("prints no text", cause.Message, StringComparison.Ordinal);
        Assert.Null(await RowOrNullAsync(Template, "1.3.0", cancellationToken));
        AssertNoCopyWritten("1.3.0");
    }

    [Fact]
    public async Task AHeaderOnlySampleWhoseDocumentPartsPrintNothingIsRefusedByTheEmptySample()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var package = SampleGate("1.0.0");
        RelabelVersion(package, "1.0.0", "1.4.0");
        package["html/document-start.html.sbn"] = """
            <html><head><meta charset="utf-8"><title>{{ header.reportTitle | html.escape }}</title><link rel="stylesheet" href="html/styles.css"></head><body>{{ if block.recordCount != "0" }}<header><h1>{{ header.reportTitle | html.escape }}</h1><p class="customer">{{ header.customer.name | html.escape }} &middot; {{ header.currency | html.escape }} &middot; {{ header.totals.recordCount | html.escape }}</p></header><p class="records">Records: {{ header.totals.recordCount | html.escape }}</p><main><table class="detail"><thead><tr><th class="col-seq">Seq</th><th class="col-date">Date</th><th class="col-description">Description</th><th class="col-qty">Qty</th><th class="col-unit">Unit</th><th class="col-amount">Amount</th></tr></thead><tbody>{{ end }}
            """u8.ToArray();
        package["html/document-end.html.sbn"] = """
            {{ if block.recordCount != "0" }}</tbody></table></main><footer><p class="clock">{{ format_date render.clock "dd/MM/yyyy HH:mm" }}</p></footer>{{ end }}</body></html>
            """u8.ToArray();
        await PublishAsync("1.4.0", package, cancellationToken);

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.4.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, cause.ErrorType);
        Assert.Contains("cannot print a report with no records", cause.Message, StringComparison.Ordinal);
        Assert.Null(await RowOrNullAsync(Template, "1.4.0", cancellationToken));
        AssertNoCopyWritten("1.4.0");
    }

    [Fact]
    public async Task AnAnchorTheTemplateDoesNotPrintIsRefusedWithAnchorMismatchAndNothingIsKept()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var package = SampleGate("1.0.0");
        RelabelVersion(package, "1.0.0", "1.5.0");
        var declaration = Encoding.UTF8.GetString(package["template.yaml"]).Replace(
            "header: [\"$.reportTitle\", \"$.customer.name\", \"$.currency\", \"$.totals.recordCount\"]",
            "header: [\"$.reportTitle\", \"$.customer.name\", \"$.currency\", \"$.totals.recordCount\", \"$.customer.id\"]",
            StringComparison.Ordinal);
        package["template.yaml"] = Encoding.UTF8.GetBytes(declaration);
        await PublishAsync("1.5.0", package, cancellationToken);

        var failure = await Assert.ThrowsAsync<WorkflowFailedException>(
            () => TakeAsync(Template, "1.5.0", Owner, cancellationToken));

        var cause = ApplicationFailure(failure);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, cause.ErrorType);
        Assert.Contains("declared as an anchor but the template does not print it as it is", cause.Message, StringComparison.Ordinal);
        Assert.Contains("template.yaml", cause.Message, StringComparison.Ordinal);
        Assert.Null(await RowOrNullAsync(Template, "1.5.0", cancellationToken));
        AssertNoCopyWritten("1.5.0");
    }

    private static Dictionary<string, byte[]> SampleGate(string version)
    {
        var folder = Path.Combine(
            RepositoryPaths.Root, "tests", "ReportService.Testing", "TemplatePackages", Template, version);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
            files[relative] = File.ReadAllBytes(file);
        }

        return files;
    }

    // The variants below start from the 1.0.0 folder and are published under a version of their
    // own; the manifest must declare the version the rig publishes, or the take refuses the
    // publication before the case's own rule is reached.
    private static void RelabelVersion(Dictionary<string, byte[]> package, string from, string to) =>
        package["template.yaml"] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(package["template.yaml"])
                .Replace("version: \"" + from + "\"", "version: \"" + to + "\"", StringComparison.Ordinal));

    private Task PublishAsync(string version, Dictionary<string, byte[]> files, CancellationToken cancellationToken) =>
        _source!.PublishAsync(Template, version, files);

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

    private async Task<StoredVersion?> RowOrNullAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<StoredVersion>(new CommandDefinition(
            """
            SELECT template_id AS "TemplateId", version AS "Version", owner_consumer_id AS "OwnerConsumerId",
                   engine AS "Engine", engine_version AS "EngineVersion", commit_sha AS "CommitSha", source_descriptor_sha256 AS "SourceDescriptorSha256",
                   package_sha256 AS "PackageSha256", object_key AS "ObjectKey"
              FROM report_service.template_versions
             WHERE template_id = @TemplateId AND version = @Version
            """,
            new { TemplateId = templateId, Version = version },
            cancellationToken: cancellationToken));
    }

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

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private string SourceRoot() => Path.Combine(_directory, "template-source");

    private sealed class StoredVersion
    {
        public string TemplateId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string OwnerConsumerId { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string EngineVersion { get; set; } = string.Empty;

        public string CommitSha { get; set; } = string.Empty;

        public string SourceDescriptorSha256 { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public string ObjectKey { get; set; } = string.Empty;
    }
}
