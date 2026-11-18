extern alias pdf;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The started PDF worker over the template source's own root: a real directory a
/// publisher writes and the worker may only read is accepted at start, the process
/// becomes ready, the <c>template-source</c> check states the provider, the class and
/// the descriptor version, a version is read through the reader resolved from that
/// host, and the process's own writable store still writes in the same process - one
/// instance that cannot write and one that can. A missing root and a file where a root
/// must be stop the start naming the setting and carrying no path.
/// </summary>
public sealed class TemplateSourceStoreTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";
    private const string Consumer = "consumer-a";

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-source-store-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;
    private int _hostPort;
    private string _connectionString = string.Empty;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public TemplateSourceStoreTests(PostgresFixture fixture) => _fixture = fixture;

    private string StorageRoot => Path.Combine(_directory, "storage");

    private string SourceRoot => Path.Combine(_directory, "template-source");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = _fixture.Application(database);
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _engine = await EngineStart.StartAsync(_engineImage, "temporal-template-source-store", cancellationToken);
        _hostPort = _engine.HostPort;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
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
    public async Task TheSourceReadsAReadOnlyRootAtStartAndStaysReady()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(SourceRoot);
        await PublishAsync();
        using var unwritable = new UnwritableDirectory(SourceRoot);
        await using var worker = Worker(SourceRoot);
        using var client = worker.CreateClient();

        await WaitForHealthyAsync(client, cancellationToken);

        using (var body = JsonDocument.Parse(await client.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken)))
        {
            Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
            var check = body.RootElement.GetProperty("checks").EnumerateArray()
                .Single(candidate => candidate.GetProperty("name").GetString() == "template-source");
            Assert.Equal("Healthy", check.GetProperty("status").GetString());
            var data = check.GetProperty("data");
            Assert.Equal("filesystem", data.GetProperty("provider").GetString());
            Assert.Equal("template-source", data.GetProperty("class").GetString());
            Assert.Equal("1.0", data.GetProperty("descriptorVersions")[0].GetString());
        }

        // The version is read through the source's own instance, and only its class is listed and opened.
        var reader = worker.Services.GetRequiredService<ITemplateSourceReader>();
        var read = await reader.ReadVersionAsync(Template, Version, cancellationToken);
        Assert.IsType<SourceVersionRead.Found>(read);

        // The process's own writable store still writes, in the same process, into its own root.
        var writable = worker.Services.GetRequiredService<IObjectStore>();
        var probe = await writable.WriteAsync(
            ObjectKey.FromSegments("staging", "probe", "pdf-worker", new string('0', 32)),
            new MemoryStream([1, 2, 3], writable: false),
            cancellationToken);
        Assert.True(probe.Succeeded, probe.Error?.Code);

        // Nothing was written into the source's own root by the start, the check or the read.
        Assert.False(Directory.Exists(Path.Combine(SourceRoot, "staging")));
    }

    [Fact]
    public void AMissingSourceRootStopsTheStartNamingTheSetting()
    {
        var missing = Path.Combine(_directory, "absent");

        using var worker = RefusingWorker(missing);

        var error = Refusal(worker);
        Assert.Equal("TemplateSource:Store:Filesystem:Root", error.Setting);
        Assert.Equal("the directory does not exist", error.Reason);
        Assert.DoesNotContain(_directory, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASourceRootThatIsAFileStopsTheStartNamingTheSetting()
    {
        var file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "x");

        using var worker = RefusingWorker(file);

        var error = Refusal(worker);
        Assert.Equal("TemplateSource:Store:Filesystem:Root", error.Setting);
        Assert.Equal("is not a directory", error.Reason);
        Assert.DoesNotContain(_directory, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task PublishAsync()
    {
        var source = await TemplateSourceClass.CreateAsync(SourceRoot);
        try
        {
            await source.PublishAsync(Template, Version, TemplatePackageFixtures.Valid(Template, Version, Consumer));
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    private WebApplicationFactory<PdfWorker> Worker(string sourceRoot) =>
        new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot);
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting("TemplateSource:Store:Filesystem:Root", sourceRoot);
            builder.UseSetting("TemplateSource:Store:Filesystem:CreateIfMissing", "false");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });

    // A host that only has to be started once: the refusal happens before any dependency is dialled.
    private WebApplicationFactory<PdfWorker> RefusingWorker(string sourceRoot) =>
        new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{ClosedPort()}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot);
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting("TemplateSource:Store:Filesystem:Root", sourceRoot);
            builder.UseSetting("TemplateSource:Store:Filesystem:CreateIfMissing", "false");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureTestServices(services => MetadataDatabaseStub.UseHealthy(services));
        });

    private static ConfigurationError Refusal(WebApplicationFactory<PdfWorker> worker)
    {
        var exception = Record.Exception(() => worker.CreateClient());
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfigurationError error)
            {
                return error;
            }
        }

        throw new Xunit.Sdk.XunitException($"The start was not refused with a configuration error; got {exception?.GetType().FullName ?? "no exception"}.");
    }

    private static async Task WaitForHealthyAsync(HttpClient client, CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            "/health answering 200",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

