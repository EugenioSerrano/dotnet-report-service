using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;

namespace ReportService.Api.Tests.Storage;

/// <summary>
/// The template source's start: it states the provider, the root, the class and the
/// descriptor contract version, reads one publication record through the source's own
/// instance, refuses a root that is missing or is a file by naming the setting and
/// never the path, and warns once when the source's root is the writable one.
/// </summary>
public sealed class TemplateSourceStartupTests : IDisposable
{
    /// <summary>The class layout this build writes; the source project is not referenced here.</summary>
    private static readonly TemplateSourceClassLayout Layout = new("template-source", "published", ".json", 64);

    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private string StorageRoot => Path.Combine(_directory.Path, "storage");

    private string SourceRoot => Path.Combine(_directory.Path, "template-source");

    [Fact]
    public async Task TheStartStatesTheProviderTheRootTheClassAndTheVersions()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(SourceRoot);
        await PublishAsync(SourceRoot, "invoice-energy", "1.1.0");
        var logs = new LogCapture();

        using (var host = Build(SourceRoot, development: true, logs))
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
        }

        var statement = Assert.Single(logs.Lines, line => line.Contains("Template source ", StringComparison.Ordinal));
        Assert.Contains("filesystem", statement, StringComparison.Ordinal);
        Assert.Contains(SourceRoot, statement, StringComparison.Ordinal);
        Assert.Contains("class template-source", statement, StringComparison.Ordinal);
        Assert.Contains("descriptor contract versions 1.0", statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStartReadsOneRecordThroughTheSourcesOwnInstanceAndWritesNothing()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(SourceRoot);
        await PublishAsync(SourceRoot, "invoice-energy", "1.1.0");
        var spy = new SourceClassStore(FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = SourceRoot },
            SourceRoot,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance));

        using (var host = Build(SourceRoot, development: true, logs: null, store: spy))
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
        }

        Assert.Contains(spy.Operations, operation => operation.Verb == "list" && operation.Target == "template-source");
        var open = Assert.Single(spy.Operations, operation => operation.Verb == "open");
        Assert.StartsWith("template-source/invoice-energy/published/1.1.0/", open.Target, StringComparison.Ordinal);
        Assert.DoesNotContain(spy.Operations, operation => operation.Verb is "write" or "delete");
        Assert.False(Directory.Exists(Path.Combine(SourceRoot, "staging")));
    }

    [Fact]
    public void AMissingSourceRootStopsTheStartNamingTheSetting()
    {
        Directory.CreateDirectory(StorageRoot);
        var missing = Path.Combine(_directory.Path, "absent");

        using var host = Build(missing, development: true, logs: null);

        var error = Refusal(host);
        Assert.Equal("TemplateSource:Store:Filesystem:Root", error.Setting);
        Assert.Equal("the directory does not exist", error.Reason);
        Assert.DoesNotContain(_directory.Path, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASourceRootThatIsAFileStopsTheStartNamingTheSetting()
    {
        Directory.CreateDirectory(StorageRoot);
        var file = Path.Combine(_directory.Path, "not-a-directory");
        File.WriteAllText(file, "x");

        using var host = Build(file, development: true, logs: null);

        var error = Refusal(host);
        Assert.Equal("TemplateSource:Store:Filesystem:Root", error.Setting);
        Assert.Equal("is not a directory", error.Reason);
        Assert.DoesNotContain(_directory.Path, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASourceRootThatMayBeCreatedIsRefused()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(SourceRoot);

        using var host = Build(SourceRoot, development: true, logs: null, createIfMissing: true);

        var error = Refusal(host);
        Assert.Equal("TemplateSource:Store:Filesystem:CreateIfMissing", error.Setting);
        Assert.Contains("must be false", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTwoRootsCoincidingIsWarnedOnceAndNotRefused()
    {
        // One directory, both instances: the development shape the decision permits.
        Directory.CreateDirectory(SourceRoot);
        var logs = new LogCapture();

        using (var host = Build(SourceRoot, development: true, logs, storageRoot: SourceRoot))
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
        }

        var warning = Assert.Single(logs.Lines, line => line.Contains("cannot express the source's read-only grant", StringComparison.Ordinal));
        Assert.StartsWith("Warning:", warning, StringComparison.Ordinal);
    }

    private IHost Build(
        string sourceRoot,
        bool development,
        LogCapture? logs,
        IObjectStore? store = null,
        bool createIfMissing = false,
        string? storageRoot = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = development ? "Development" : "Production",
            ContentRootPath = _directory.Path,
        });
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Storage:Provider"] = "filesystem",
            ["Storage:Filesystem:Root"] = storageRoot ?? StorageRoot,
            ["Storage:Filesystem:CreateIfMissing"] = "true",
            ["TemplateSource:Store:Filesystem:Root"] = sourceRoot,
            ["TemplateSource:Store:Filesystem:CreateIfMissing"] = createIfMissing ? "true" : "false",
        };
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddObjectStorage("pdf-worker");
        builder.AddTemplateSourceStorage(Layout, ["1.0"]);
        if (store is not null)
        {
            builder.Services.RemoveAllKeyed<IObjectStore>(TemplateSourceStorage.StoreServiceKey);
            builder.Services.AddKeyedSingleton<IObjectStore>(TemplateSourceStorage.StoreServiceKey, store);
        }

        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        return builder.Build();
    }

    private static ConfigurationError Refusal(IHost host)
    {
        var exception = Record.Exception(host.Start);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfigurationError error)
            {
                return error;
            }
        }

        throw new Xunit.Sdk.XunitException($"The start was not refused with a configuration error; got {exception?.GetType().FullName ?? "no exception"}.");
    }

    // The record as the owner's pipeline writes it: its object name is its own SHA-256.
    private static async Task PublishAsync(string root, string template, string version)
    {
        Directory.CreateDirectory(root);
        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root, CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var record = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $$"""{"descriptorVersion":"1.0","templateId":"{{template}}","version":"{{version}}"}"""));
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(record));
        var key = ObjectKey.FromSegments("template-source", template, "published", version, sha256 + ".json");
        var write = await store.WriteAsync(key, new MemoryStream(record, writable: false), TestContext.Current.CancellationToken);
        Assert.True(write.Succeeded, write.Error?.Code);
    }
}
