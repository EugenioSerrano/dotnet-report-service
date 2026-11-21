using System.Globalization;
using System.Runtime.CompilerServices;
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
    public async Task TheStartReadsThroughTheFirstTemplatesPublishedPrefixAndNeverScansTheClass()
    {
        Directory.CreateDirectory(StorageRoot);
        var real = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = SourceRoot, CreateIfMissing = true },
            SourceRoot,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var first = await WriteFileAsync(real, "invoice-energy", "1.1.0");
        await PublishAsync(SourceRoot, "invoice-energy", "1.1.0");
        await PublishAsync(SourceRoot, "invoice-utilities", "2.0.0");
        var spy = new SourceClassStore(real);
        var store = new FirstEntryStore(spy, first);

        using (var host = Build(SourceRoot, development: true, logs: null, store: store))
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
        }

        var lists = spy.Operations
            .Where(operation => operation.Verb == "list")
            .Select(operation => operation.Target)
            .ToList();
        Assert.Equal(["template-source", "template-source/invoice-energy/published"], lists);
        var open = Assert.Single(spy.Operations, operation => operation.Verb == "open");
        Assert.StartsWith("template-source/invoice-energy/published/1.1.0/", open.Target, StringComparison.Ordinal);
        Assert.DoesNotContain(spy.Operations, operation => operation.Verb is "write" or "delete");
        Assert.DoesNotContain(lists, target => target.Contains("invoice-utilities", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStartStopsAfterListabilityWhenTheFirstTemplatePublishesNothing()
    {
        Directory.CreateDirectory(StorageRoot);
        var real = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = SourceRoot, CreateIfMissing = true },
            SourceRoot,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var first = await WriteFileAsync(real, "invoice-energy", "1.1.0");
        var spy = new SourceClassStore(real);
        var store = new FirstEntryStore(spy, first);

        using (var host = Build(SourceRoot, development: true, logs: null, store: store))
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
        }

        var lists = spy.Operations
            .Where(operation => operation.Verb == "list")
            .Select(operation => operation.Target)
            .ToList();
        Assert.Equal(["template-source", "template-source/invoice-energy/published"], lists);
        Assert.DoesNotContain(spy.Operations, operation => operation.Verb == "open");
        Assert.DoesNotContain(spy.Operations, operation => operation.Verb is "write" or "delete");
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

    // The check's two halves held on the test's own gates, and the clock moved by the case: the
    // listing consumes four of its bound's seconds and the read answers at its own two, so six
    // seconds of work complete where one shared budget would have cancelled the read at five.
    // Everything else is the real Filesystem provider's behaviour, and the record it answers
    // with is the one the check reads.
    [Fact]
    public async Task TheListingsBudgetDoesNotCutTheReadShort()
    {
        Directory.CreateDirectory(StorageRoot);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var scripted = Scripted();
        var logs = new LogCapture();
        using var host = Build(SourceRoot, development: true, logs, store: scripted, clock: clock);
        var start = host.StartAsync(TestContext.Current.CancellationToken);

        await scripted.ListingEntered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(4));
        scripted.ReleaseListing();
        await scripted.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));

        var failure = start.IsCompleted ? await Record.ExceptionAsync(() => start) : null;
        Assert.True(failure is null, $"the start was cancelled after six seconds of work: {failure}");

        scripted.ReleaseRead();
        await start;
        Assert.Contains(logs.Lines, line => line.Contains("Template source ", StringComparison.Ordinal));
    }

    // The read's own bound: two seconds into the read nothing has fired, and the three more that
    // reach its five refuse the start with the constant the code already names. The listings
    // consumed four seconds first, so a shared budget would have fired one second in.
    [Fact]
    public async Task TheReadsOwnBoundRefusesWithTheNamedSetting()
    {
        Directory.CreateDirectory(StorageRoot);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var scripted = Scripted();
        using var host = Build(SourceRoot, development: true, logs: null, store: scripted, clock: clock);
        var start = host.StartAsync(TestContext.Current.CancellationToken);

        await scripted.ListingEntered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(4));
        scripted.ReleaseListing();
        await scripted.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));

        var early = start.IsCompleted ? await Record.ExceptionAsync(() => start) : null;
        Assert.True(early is null, $"the start was cancelled two seconds into the read: {early}");

        clock.Advance(TimeSpan.FromSeconds(3));

        var exception = await Record.ExceptionAsync(() => start);
        Assert.True(
            Flatten(exception) is ConfigurationError,
            $"the start ended with {Describe(exception)} instead of the refusal naming the setting");
        var error = (ConfigurationError)Flatten(exception)!;
        Assert.Equal("TemplateSource:Store:Filesystem:Root", error.Setting);
        Assert.Equal(TemplateSourceStartup.CannotBeRead, error.Reason);
        Assert.DoesNotContain(_directory.Path, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostStopDuringTheReadIsAnOrdinaryStop()
    {
        Directory.CreateDirectory(StorageRoot);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var scripted = Scripted();
        var logs = new LogCapture();
        using var host = Build(SourceRoot, development: true, logs, store: scripted, clock: clock);
        using var stop = new CancellationTokenSource();
        var start = host.StartAsync(stop.Token);

        await scripted.ListingEntered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        scripted.ReleaseListing();
        await scripted.ReadEntered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await stop.CancelAsync();

        var failure = await Record.ExceptionAsync(() => start);
        Assert.True(failure is null, $"the start did not stop cleanly: {failure}");
        Assert.Single(logs.Lines, line => line.Contains("stopped before it completed", StringComparison.Ordinal));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AHostStopDuringTheListingIsAnOrdinaryStop()
    {
        Directory.CreateDirectory(StorageRoot);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var scripted = Scripted();
        var logs = new LogCapture();
        using var host = Build(SourceRoot, development: true, logs, store: scripted, clock: clock);
        using var stop = new CancellationTokenSource();
        var start = host.StartAsync(stop.Token);

        await scripted.ListingEntered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await stop.CancelAsync();

        var failure = await Record.ExceptionAsync(() => start);
        Assert.True(failure is null, $"the start did not stop cleanly: {failure}");
        Assert.Single(logs.Lines, line => line.Contains("stopped before it completed", StringComparison.Ordinal));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // The delivered classification of a genuinely unreadable record: the bytes do not match the
    // hash the record's name carries, and the start refuses with the setting and the reason the
    // code already names. It is the process case's own trigger, proved here at the check's level.
    [Fact]
    public async Task ARecordThatCannotBeReadStopsTheStartWithTheNamedSetting()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(SourceRoot);
        var source = await TemplateSourceClass.CreateAsync(SourceRoot);
        var record = Encoding.UTF8.GetBytes(
            """{"descriptorVersion":"1.0","templateId":"invoice-energy","version":"1.1.0","files":[],"fileCount":0,"totalSize":0}""");
        var key = await source.WriteRecordAsync("invoice-energy", "1.1.0", record);
        source.Corrupt(
            key,
            Encoding.UTF8.GetBytes(
                """{"descriptorVersion":"1.0","templateId":"invoice-energy","version":"1.1.0","files":[],"fileCount":0,"totalSize":17}"""));

        using var host = Build(SourceRoot, development: true, logs: null);

        var error = Refusal(host);
        Assert.Equal("TemplateSource:Store:Filesystem:Root", error.Setting);
        Assert.Equal(TemplateSourceStartup.CannotBeRead, error.Reason);
        Assert.DoesNotContain(_directory.Path, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The record the check reads: one publication record whose bytes match the hash its name
    // carries, answered only when the case releases the read.
    private ScriptedSourceStore Scripted()
    {
        var content = Encoding.UTF8.GetBytes("""{"descriptorVersion":"1.0"}""");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var entry = new ObjectInfo(
            ObjectKey.FromSegments("template-source", "invoice-energy", "published", "1.1.0", sha256 + ".json"),
            content.LongLength,
            DateTimeOffset.UnixEpoch);
        var inner = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot, CreateIfMissing = true },
            StorageRoot,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        return new ScriptedSourceStore(inner, entry, content);
    }

    private static Exception? Flatten(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfigurationError)
            {
                return current;
            }
        }

        return null;
    }

    private static string Describe(Exception? exception) =>
        exception is null ? "no exception" : $"{exception.GetType().FullName}: {exception.Message}";

    private IHost Build(
        string sourceRoot,
        bool development,
        LogCapture? logs,
        IObjectStore? store = null,
        bool createIfMissing = false,
        string? storageRoot = null,
        TimeProvider? clock = null)
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
        if (clock is not null)
        {
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton(clock);
        }

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

    // One template file of a version, as the publisher's pipeline writes it: the first entry
    // of the class listing in the two cases below, where the record is reached through the
    // published prefix of that same template.
    private static async Task<ObjectInfo> WriteFileAsync(IObjectStore store, string template, string version)
    {
        var bytes = Encoding.UTF8.GetBytes($"template: {template} {version}");
        var key = ObjectKey.FromSegments("template-source", template, "files", version, "template.yaml");
        var write = await store.WriteAsync(key, new MemoryStream(bytes, writable: false), TestContext.Current.CancellationToken);
        Assert.True(write.Succeeded, write.Error?.Code);
        return new ObjectInfo(key, bytes.LongLength, DateTimeOffset.UnixEpoch);
    }

    // The first entry a class listing answers with, supplied deterministically because a
    // directory's enumeration order is not a contract; every other operation, and the rest of
    // the class listing, is the real store's, so a scan still sees the class it would see.
    // The check's class listing and its verified read, held on the test's own gates: each half
    // answers when the case releases it, so the case decides when the check's time passes. It is
    // a fault injector for the check, never a provider contract; only those two calls are the
    // test's, and every other operation is the real store's.
    private sealed class ScriptedSourceStore(IObjectStore inner, ObjectInfo entry, byte[] content) : IObjectStore
    {
        private readonly TaskCompletionSource _listingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _listingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ListingEntered => _listingEntered.Task;

        public Task ReadEntered => _readEntered.Task;

        public void ReleaseListing() => _listingRelease.TrySetResult();

        public void ReleaseRead() => _readRelease.TrySetResult();

        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            inner.WriteAsync(key, content, cancellationToken);

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetInfoAsync(key, cancellationToken);

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, cancellationToken);

        public async IAsyncEnumerable<ObjectInfo> ListAsync(
            string classPrefix,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _listingEntered.TrySetResult();
            await _listingRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return entry;
        }

        public async Task<StorageResult<VerifiedRead>> OpenReadAsync(
            ObjectKey key,
            long expectedSize,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            _readEntered.TrySetResult();
            await _readRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return StorageResult.Success(new VerifiedRead(new MemoryStream(content, writable: false), expectedSize, expectedSha256));
        }
    }

    private sealed class FirstEntryStore(IObjectStore inner, ObjectInfo first) : IObjectStore
    {
        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            inner.WriteAsync(key, content, cancellationToken);

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetInfoAsync(key, cancellationToken);

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, cancellationToken);

        public async IAsyncEnumerable<ObjectInfo> ListAsync(
            string classPrefix,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (string.Equals(classPrefix, "template-source", StringComparison.Ordinal))
            {
                var rest = new List<ObjectInfo>();
                await foreach (var entry in inner.ListAsync(classPrefix, cancellationToken).ConfigureAwait(false))
                {
                    rest.Add(entry);
                }

                yield return first;
                foreach (var entry in rest)
                {
                    if (!string.Equals(entry.Key.ToString(), first.Key.ToString(), StringComparison.Ordinal))
                    {
                        yield return entry;
                    }
                }

                yield break;
            }

            await foreach (var entry in inner.ListAsync(classPrefix, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
    }
}
