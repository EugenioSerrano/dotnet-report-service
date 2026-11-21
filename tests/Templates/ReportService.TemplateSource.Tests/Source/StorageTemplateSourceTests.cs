using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplateSource.Source;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;

namespace ReportService.TemplateSource.Tests.Source;

/// <summary>
/// The source read against the real Filesystem provider: the publication record
/// is opened with the hash its own name carries and the size the listing gave,
/// every file is read only against the size and SHA-256 the record declares, and
/// absent, incomplete, duplicate, unreadable and invalid are told apart. The
/// read is given one store and never writes to it.
/// </summary>
public sealed class StorageTemplateSourceTests : IDisposable
{
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";
    private const string OtherConsumer = "reporting-portal";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-source-read",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private readonly LogCapture _logs = new();

    private readonly ILoggerFactory _loggerFactory;

    public StorageTemplateSourceTests() =>
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));

    public void Dispose()
    {
        _loggerFactory.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task ARecordIsReadVerifiedAgainstItsOwnNameAndTheListingsSize()
    {
        await using var source = await PublishAsync();

        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        var found = Assert.IsType<SourceVersionRead.Found>(read);
        var publication = found.Publication;
        Assert.Equal(Template, publication.TemplateId);
        Assert.Equal(Version, publication.Version);
        Assert.Equal(5, publication.FileCount);
        Assert.Equal(64, publication.DescriptorSha256.Length);
        Assert.Equal(publication.DescriptorSha256, publication.DescriptorKey.Segments[^1][..64]);
        Assert.Equal(publication.Files.Sum(file => file.Size), publication.TotalSize);
        Assert.Contains(publication.Files, file => file.Path == "template.yaml");
    }

    [Fact]
    public async Task ARecordWhoseBytesDoNotMatchTheHashInItsNameIsRefusedAndNeverParsed()
    {
        await using var source = await PublishAsync();
        var record = source.Writes.Single(key => key.Contains("/published/", StringComparison.Ordinal));
        var key = ObjectKey.Create(record);

        // The record is replaced behind the store's back: its name keeps the old hash.
        source.Corrupt(key, Encoding.UTF8.GetBytes("""{"descriptorVersion":"1.0"}"""));

        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<SourceVersionRead.Refused>(read);
        Assert.Equal(SourceReadRefusalKind.Invalid, refused.Refusal.Kind);
        Assert.Contains("does not match the hash its name declares", refused.Refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContractThisBuildDoesNotReadIsUnsupportedAndNamesIt()
    {
        await using var source = await Fixture();
        await WriteRawRecordAsync(source, Template, Version, """{"descriptorVersion":"2.0","templateId":"invoice-energy","version":"1.1.0","files":[],"fileCount":0,"totalSize":0}""");

        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<SourceVersionRead.Refused>(read);
        Assert.Equal(SourceReadRefusalKind.Unsupported, refused.Refusal.Kind);
        Assert.Contains("2.0", refused.Refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublicationThatDeclaresAnotherVersionIsRefusedNamingBoth()
    {
        await using var source = await Fixture();
        await WriteRawRecordAsync(
            source,
            Template,
            Version,
            """{"descriptorVersion":"1.0","templateId":"invoice-energy","version":"1.2.0","files":[{"path":"template.yaml","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"fileCount":1,"totalSize":1}""");

        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<SourceVersionRead.Refused>(read);
        Assert.Equal(SourceReadRefusalKind.Invalid, refused.Refusal.Kind);
        Assert.Contains(Version, refused.Refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("1.2.0", refused.Refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileTheRecordNamesThatTheStoreDoesNotHoldIsRefusedNamingThePath()
    {
        await using var source = await Fixture();
        await source.PublishRecordOnlyAsync(
            Template,
            Version,
            [("template.yaml", 4, new string('a', 64)), ("schema.json", 2, new string('b', 64))]);
        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var found = Assert.IsType<SourceVersionRead.Found>(read);

        var failure = await Assert.ThrowsAsync<SourceReadException>(() => source.Reader().OpenFileAsync(
            Template,
            Version,
            found.Publication.Files.Single(file => file.Path == "template.yaml"),
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken));

        Assert.Equal(SourceReadFailureKind.Invalid, failure.Kind);
        Assert.Contains("template.yaml", failure.Message, StringComparison.Ordinal);
        Assert.Contains("does not hold", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlippedByteIsRefusedNamingThePackagePathAndNeverTheKey()
    {
        await using var source = await PublishAsync();
        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var found = Assert.IsType<SourceVersionRead.Found>(read);
        var file = found.Publication.Files.Single(entry => entry.Path == "schema.json");
        var key = TemplateSourceKeys.File(Template, Version, file.Path);
        source.Corrupt(key, Encoding.UTF8.GetBytes("{\"changed\":true}"));

        var failure = await Assert.ThrowsAsync<SourceReadException>(() => source.Reader().OpenFileAsync(
            Template,
            Version,
            file,
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken));

        Assert.Equal(SourceReadFailureKind.Invalid, failure.Kind);
        Assert.Contains("schema.json", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("template-source/", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATruncatedObjectIsRefusedNamingThePackagePath()
    {
        await using var source = await PublishAsync();
        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var found = Assert.IsType<SourceVersionRead.Found>(read);
        var file = found.Publication.Files.Single(entry => entry.Path == "html/row.html.sbn");
        source.Corrupt(TemplateSourceKeys.File(Template, Version, file.Path), Encoding.UTF8.GetBytes("<tr"));

        var failure = await Assert.ThrowsAsync<SourceReadException>(() => source.Reader().OpenFileAsync(
            Template,
            Version,
            file,
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken));

        Assert.Equal(SourceReadFailureKind.Invalid, failure.Kind);
        Assert.Contains("html/row.html.sbn", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRecordAndNoFileIsAbsent()
    {
        await using var source = await Fixture();

        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        Assert.IsType<SourceVersionRead.Absent>(read);
    }

    [Fact]
    public async Task FilesWithoutARecordAreIncomplete()
    {
        await using var source = await Fixture();
        await source.WriteFileAsync(Template, Version, "template.yaml", Encoding.UTF8.GetBytes("manifestVersion: \"1.0\""));
        await source.WriteFileAsync(Template, Version, "schema.json", Encoding.UTF8.GetBytes("{}"));

        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        var incomplete = Assert.IsType<SourceVersionRead.Incomplete>(read);
        Assert.Equal(2, incomplete.FileCount);
    }

    [Fact]
    public async Task ARecordWrittenAfterwardsIsTheFirstPublication()
    {
        await using var source = await Fixture();
        await source.WriteFileAsync(Template, Version, "template.yaml", Encoding.UTF8.GetBytes("manifestVersion: \"1.0\""));

        Assert.IsType<SourceVersionRead.Incomplete>(await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken));

        await source.PublishAsync(Template, Version, Package());

        Assert.IsType<SourceVersionRead.Found>(await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MoreThanOneRecordIsTheDuplicateStateAndNoneIsPicked()
    {
        await using var source = await Fixture();
        await source.PublishRecordOnlyAsync(Template, Version, [("template.yaml", 1, new string('a', 64))]);
        await WriteRawRecordAsync(
            source,
            Template,
            Version,
            """{"descriptorVersion":"1.0","templateId":"invoice-energy","version":"1.1.0","files":[{"path":"template.yaml","size":1,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}],"fileCount":1,"totalSize":1}""");

        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        var duplicate = Assert.IsType<SourceVersionRead.Duplicate>(read);
        Assert.Equal(2, duplicate.Descriptors.Count);
        Assert.All(duplicate.Descriptors, descriptor => Assert.Equal(64, descriptor.Sha256.Length));
    }

    [Fact]
    public async Task AClassThatCannotBeListedOrReadIsUnavailableAndNeverNotFound()
    {
        var store = new FailingStore();
        var source = new StorageTemplateSource(store, new TemplateSourceOptions(), new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance);

        Assert.IsType<SourceVersionRead.Unreachable>(await source.ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken));

        var failure = await Assert.ThrowsAsync<TemplateSourceException>(() => source.ListPublishedVersionsAsync(Template, TestContext.Current.CancellationToken));
        Assert.Equal(TemplateSourceFailure.Unreachable, failure.Failure);
    }

    [Fact]
    public async Task TheReadNeverWritesOrDeletes()
    {
        await using var source = await PublishAsync();
        var guard = new GuardStore(source.Store);
        var reader = new StorageTemplateSource(guard, new TemplateSourceOptions(), new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance);

        var read = await reader.ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var found = Assert.IsType<SourceVersionRead.Found>(read);
        await reader.OpenFileAsync(
            Template,
            Version,
            found.Publication.Files.Single(file => file.Path == "template.yaml"),
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken);
        await reader.ListPublishedVersionsAsync(Template, TestContext.Current.CancellationToken);

        Assert.Equal(0, guard.Writes);
        Assert.Equal(0, guard.Deletes);
        Assert.NotEmpty(source.Writes);
    }

    [Fact]
    public async Task ALayoutEntryOutsideTheRecordNamesIsSkippedAndLogged()
    {
        await using var source = await PublishAsync();
        await source.WriteRawFileAsync(
            $"template-source/{Template}/published/{Version}/readme.txt",
            Encoding.UTF8.GetBytes("not a record"));

        var reader = new StorageTemplateSource(
            source.Store,
            new TemplateSourceOptions(),
            new TemplateSourceMetrics(),
            _loggerFactory.CreateLogger<StorageTemplateSource>());
        var read = await reader.ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        Assert.IsType<SourceVersionRead.Found>(read);
        Assert.Contains(_logs.Lines, line => line.Contains("template.source.skipped_entry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHashedVersionSegmentIsEnumeratedByReadingItsRecord()
    {
        const string version = "1.1.0+build.1";
        await using var source = await Fixture();
        await source.PublishAsync(Template, version, Package(version));

        var versions = await source.Reader().ListPublishedVersionsAsync(Template, TestContext.Current.CancellationToken);

        var published = Assert.Single(versions);
        Assert.Equal(version, published.Version);
        Assert.Equal(64, published.DescriptorSha256.Length);
    }

    [Fact]
    public async Task TwoRecordsUnderOneVersionAreTwoEntries()
    {
        await using var source = await Fixture();
        await source.PublishRecordOnlyAsync(Template, Version, [("template.yaml", 1, new string('a', 64))]);
        await WriteRawRecordAsync(
            source,
            Template,
            Version,
            """{"descriptorVersion":"1.0","templateId":"invoice-energy","version":"1.1.0","files":[{"path":"template.yaml","size":1,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}],"fileCount":1,"totalSize":1}""");

        var versions = await source.Reader().ListPublishedVersionsAsync(Template, TestContext.Current.CancellationToken);

        Assert.Equal(2, versions.Count);
        Assert.All(versions, entry => Assert.Equal(Version, entry.Version));
    }

    [Fact]
    public async Task AnEntryOutsideTheLayoutIsNotEnumerated()
    {
        await using var source = await PublishAsync();
        await source.WriteRawFileAsync(
            $"template-source/{Template}/published/{Version}/not-a-record.json",
            Encoding.UTF8.GetBytes("{}"));

        var versions = await source.Reader().ListPublishedVersionsAsync(Template, TestContext.Current.CancellationToken);

        Assert.Single(versions);
    }

    [Fact]
    public async Task ANameThatCannotBeAVersionAnswersAbsentWithoutAStoreCall()
    {
        var store = new NoStoreCalls();
        var source = new StorageTemplateSource(store, new TemplateSourceOptions(), new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance);

        Assert.IsType<SourceVersionRead.Absent>(await source.ReadVersionAsync(Template, "not a version", TestContext.Current.CancellationToken));
        Assert.IsType<SourceVersionRead.Absent>(await source.ReadVersionAsync("Not A Template", Version, TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task AVersionsStablePrefixIsNotItsPreReleaseSiblingsRecord()
    {
        await using var source = await Fixture();
        await source.PublishAsync(Template, Version + "-rc.1", Package(Version + "-rc.1"));

        var read = await source.Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        Assert.IsType<SourceVersionRead.Absent>(read);
    }

    private async Task<TemplateSourceClass> Fixture()
    {
        Directory.CreateDirectory(_root);
        return await TemplateSourceClass.CreateAsync(_root);
    }

    private async Task<TemplateSourceClass> PublishAsync()
    {
        var source = await Fixture();
        await source.PublishAsync(Template, Version, Package());
        return source;
    }

    private static Dictionary<string, byte[]> Package(string version = Version) => new(StringComparer.Ordinal)
    {
        ["template.yaml"] = Encoding.UTF8.GetBytes(
            $"""
            manifestVersion: "1.0"
            id: {Template}
            version: "{version}"
            owner: billing-system
            shared: false
            engine: scriban
            engineVersion: "7.5.1"
            dataContract:
              version: "1.0"
              profile: header-items-v1
              schema: schema.json
            outputs:
              html:
                parts:
                  documentStart: html/document-start.html.sbn
                  row: html/row.html.sbn
                  documentEnd: html/document-end.html.sbn
            """),
        ["schema.json"] = Encoding.UTF8.GetBytes("{}"),
        ["html/document-start.html.sbn"] = Encoding.UTF8.GetBytes("<html><body>"),
        ["html/row.html.sbn"] = Encoding.UTF8.GetBytes("<tr></tr>"),
        ["html/document-end.html.sbn"] = Encoding.UTF8.GetBytes("</body></html>"),
    };

    private static Task WriteRawRecordAsync(TemplateSourceClass source, string templateId, string version, string json) =>
        source.WriteRecordAsync(templateId, version, Encoding.UTF8.GetBytes(json));

    /// <summary>A store whose writes and deletes fail the suite if the read ever reaches them.</summary>
    private sealed class GuardStore(IObjectStore inner) : IObjectStore
    {
        public int Writes { get; private set; }

        public int Deletes { get; private set; }

        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken)
        {
            Writes++;
            throw new InvalidOperationException("the read must never write");
        }

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetInfoAsync(key, cancellationToken);

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            Deletes++;
            throw new InvalidOperationException("the read must never delete");
        }

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            inner.ListAsync(classPrefix, cancellationToken);
    }

    /// <summary>A store that answers the outage the read must keep apart from absence.</summary>
    private sealed class FailingStore : IObjectStore
    {
        public StorageCapabilities Capabilities => FilesystemObjectStore.DeclaredCapabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) =>
            Task.FromException<StorageResult<VerifiedRead>>(Failure());

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Task.FromException<StorageResult<ObjectInfo>>(Failure());

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            throw Failure();
#pragma warning disable CS0162 // The compiler sees the throw above; the yield makes this an iterator.
            yield break;
#pragma warning restore CS0162
        }

        private static StorageException Failure() =>
            new(StorageOutcome.Transient, new StorageError("storage.transient", "The object store did not answer."));
    }

    /// <summary>A store that fails the suite when the read calls it at all.</summary>
    private sealed class NoStoreCalls : IObjectStore
    {
        public int Calls { get; private set; }

        public StorageCapabilities Capabilities => FilesystemObjectStore.DeclaredCapabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) => Call<ObjectWrite>();

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken) => Call<VerifiedRead>();

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) => Call<ObjectInfo>();

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) => Call<bool>();

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) => new ThrowingEnumerable(this);

        private Task<StorageResult<T>> Call<T>()
        {
            Calls++;
            throw new InvalidOperationException("the read must not call the store for a name that cannot be a version");
        }

        private sealed class ThrowingEnumerable(NoStoreCalls owner) : IAsyncEnumerable<ObjectInfo>
        {
            public async IAsyncEnumerator<ObjectInfo> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                owner.Calls++;
                throw new InvalidOperationException("the read must not call the store for a name that cannot be a version");
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }
        }
    }
}
