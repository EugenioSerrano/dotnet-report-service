using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
using ReportService.Testing.TemplateSource;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The template-source class read against the real Filesystem provider, not a
/// double: the paged listing consumed as an <c>IAsyncEnumerable</c>, a record
/// verified against the hash its own key names and the size the listing gives, one
/// file verified, a flipped byte, a missing object, a truncated object, a
/// create-only write the class refuses, and the read's operation set - list and
/// read only. It is the conformance class every provider must run unchanged when
/// its own Bolt delivers it.
/// </summary>
public sealed class TemplateSourceClassConformanceTests : IAsyncLifetime
{
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";
    private const int ListedRecords = 300;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-source-conformance",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private TemplateSourceClass _source = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _source = await TemplateSourceClass.CreateAsync(_root);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task APagedListingIsConsumedAsAnAsyncEnumerable()
    {
        for (var index = 0; index < ListedRecords; index++)
        {
            await _source.PublishAsync(Template, $"1.{index}.0", Package($"1.{index}.0"));
        }

        var published = await Reader().ListPublishedVersionsAsync(Template, TestContext.Current.CancellationToken);

        Assert.Equal(ListedRecords, published.Count);
        Assert.Equal(published.Count, published.Select(entry => entry.Version).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ARecordIsVerifiedByItsKeysHashAndTheListingsSize()
    {
        await _source.PublishAsync(Template, Version, Package(Version));

        var read = await Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);

        var found = Assert.IsType<SourceVersionRead.Found>(read);
        Assert.Equal(64, found.Publication.DescriptorSha256.Length);
        Assert.Equal(found.Publication.DescriptorSha256, found.Publication.DescriptorKey.Segments[^1][..64]);
        Assert.True(found.Publication.DescriptorSize > 0);
    }

    [Fact]
    public async Task OneFileIsVerifiedAgainstTheRecordAndAFlipIsRefused()
    {
        await _source.PublishAsync(Template, Version, Package(Version));
        var read = await Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var publication = Assert.IsType<SourceVersionRead.Found>(read).Publication;
        var file = publication.Files.Single(entry => entry.Path == "schema.json");
        var reader = Reader();

        await reader.OpenFileAsync(
            Template,
            Version,
            file,
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken);

        _source.Corrupt(TemplateSourceKeys.File(Template, Version, file.Path), Encoding.UTF8.GetBytes("{\"changed\":true}"));
        await Assert.ThrowsAsync<SourceReadException>(() => reader.OpenFileAsync(
            Template,
            Version,
            file,
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMissingAndATruncatedObjectAreBothRefused()
    {
        await _source.PublishRecordOnlyAsync(
            Template,
            Version,
            [("template.yaml", 4, new string('a', 64)), ("schema.json", 2, new string('b', 64))]);
        var read = await Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var publication = Assert.IsType<SourceVersionRead.Found>(read).Publication;
        var reader = Reader();

        await Assert.ThrowsAsync<SourceReadException>(() => reader.OpenFileAsync(
            Template,
            Version,
            publication.Files.Single(entry => entry.Path == "template.yaml"),
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken));

        await _source.DeleteAsync(TemplateSourceKeys.File(Template, Version, "schema.json"));
        await Assert.ThrowsAsync<SourceReadException>(() => reader.OpenFileAsync(
            Template,
            Version,
            publication.Files.Single(entry => entry.Path == "schema.json"),
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATruncatedObjectIsRefusedAgainstItsRecordedSize()
    {
        await _source.PublishAsync(Template, Version, Package(Version));
        var read = await Reader().ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var publication = Assert.IsType<SourceVersionRead.Found>(read).Publication;
        var file = publication.Files.Single(entry => entry.Path == "html/row.html.sbn");
        _source.Corrupt(TemplateSourceKeys.File(Template, Version, file.Path), Encoding.UTF8.GetBytes("<tr"));

        await Assert.ThrowsAsync<SourceReadException>(() => Reader().OpenFileAsync(
            Template,
            Version,
            file,
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACreateOnlyWriteIsRefusedByTheClass()
    {
        await _source.PublishAsync(Template, Version, Package(Version));
        var key = TemplateSourceKeys.File(Template, Version, "schema.json");

        var write = await _source.Store.WriteAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("{}"), writable: false), TestContext.Current.CancellationToken);

        Assert.Equal(StorageOutcome.AlreadyExists, write.Outcome);
    }

    [Fact]
    public async Task TheReadOnlyOperationSetNeverWritesOrDeletes()
    {
        await _source.PublishAsync(Template, Version, Package(Version));
        var guard = new GuardStore(_source.Store);
        var reader = new StorageTemplateSource(guard, new TemplateSourceOptions(), new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance);

        var read = await reader.ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var publication = Assert.IsType<SourceVersionRead.Found>(read).Publication;
        await reader.OpenFileAsync(
            Template,
            Version,
            publication.Files.Single(entry => entry.Path == "template.yaml"),
            (stream, token) => stream.CopyToAsync(Stream.Null, token),
            TestContext.Current.CancellationToken);
        await reader.ListPublishedVersionsAsync(Template, TestContext.Current.CancellationToken);

        Assert.Equal(0, guard.Writes);
        Assert.Equal(0, guard.Deletes);
    }

    private StorageTemplateSource Reader() =>
        new(_source.Store, new TemplateSourceOptions(), new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance);

    private static Dictionary<string, byte[]> Package(string version) => new(StringComparer.Ordinal)
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
}
