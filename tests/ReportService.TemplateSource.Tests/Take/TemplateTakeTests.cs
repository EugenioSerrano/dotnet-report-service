using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ManifestReader = ReportService.TemplateSource.Manifest.TemplateManifestReader;

namespace ReportService.TemplateSource.Tests.Take;

/// <summary>
/// The take, at service level, over the source read on the real Filesystem
/// provider: one version's publication is read verified, kept as one write-once
/// object under the content-addressed key and made usable by an insert-once
/// record. The cases are the acceptance criteria of the version-taking story: the
/// first take and its record; a taken version that is never read again; a version
/// whose source cannot be read; the four states of the read mapped to the take's
/// codes; the record's own defects; the manifest's version rule; another
/// consumer's version, answered like an absent one before any content refusal;
/// the manifest read first and the gate before every other object; and a take
/// that lost the race to another take.
/// </summary>
public sealed class TemplateTakeTests : IAsyncDisposable
{
    private const string Consumer = "billing-system";
    private const string OtherConsumer = "reporting-portal";
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-take",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    private readonly InMemoryTemplateVersionStore _versions = new();

    private readonly LogCapture _logs = new();

    private readonly ILoggerFactory _loggerFactory;

    private readonly List<ITemplateVersionCheck> _checks = [];

    private readonly TemplateSourceClass _source;

    private readonly TemplateSourceOptions _options = new();

    public TemplateTakeTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_root);
        _source = TemplateSourceClass.CreateAsync(Path.Combine(_root, "source")).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
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
    public async Task TheFirstTakeKeepsTheVersionAsAWriteOnceObjectAndRecordsIt()
    {
        await _source.PublishAsync(Template, Version, Package());

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var record = AssertTaken(outcome);
        Assert.Equal(Template, record.TemplateId);
        Assert.Equal(Version, record.Version);
        Assert.Equal(Consumer, record.OwnerConsumerId);
        Assert.False(record.Shared);
        Assert.Null(record.CommitSha);
        Assert.Null(record.SourceTreeSha);
        Assert.Equal(64, record.SourceDescriptorSha256!.Length);
        Assert.Equal($"template-source/{Template}/published/{Version}/{record.SourceDescriptorSha256}.json", record.SourceDescriptorKey);
        Assert.Equal(TemplateEngine.Scriban, record.Engine);
        Assert.Equal(TemplateEngineCatalog.ScribanVersion, record.EngineVersion);
        Assert.Equal("schema.json", record.SchemaRef);
        Assert.Equal(5, record.FileCount);
        Assert.Equal(_clock.Now, record.TakenAt);
        Assert.Single(_versions.Records);

        // The verified per-file list is ordered by path ordinal, whatever the record's order.
        Assert.Equal(
            ["html/document-end.html.sbn", "html/document-start.html.sbn", "html/row.html.sbn", "schema.json", "template.yaml"],
            record.SourceObjects!.Select(file => file.Path));

        // The key is the content-addressed one, and the object reads back verified.
        Assert.Equal($"templates/{Consumer}/{Template}/{Version}/{record.PackageSha256}", record.ObjectKey.ToString());
        var read = await _source.Store.OpenReadAsync(record.ObjectKey, record.PackageSize, record.PackageSha256, TestContext.Current.CancellationToken);
        Assert.True(read.Succeeded, read.Error?.Message);
        await using (var verified = read.Value!)
        {
            await verified.Content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        }

        Assert.Contains(
            _logs.Lines,
            line => line.Contains("version 1.1.0", StringComparison.Ordinal) && line.Contains("ended taken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATakenVersionIsUsedAgainWithoutReadingTheSource()
    {
        await _source.PublishAsync(Template, Version, Package());
        await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        // The source is counted, so the zero is proved rather than assumed.
        var operations = new OperationStore(_source.Store);
        var writes = _source.Writes.Count;
        var outcome = await Take(reader: new StorageTemplateSource(operations, _options, new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance))
            .TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        Assert.IsType<AlreadyTakenVersion>(outcome);
        Assert.Empty(operations.Opened);
        Assert.Equal(0, operations.ListCalls);
        Assert.Equal(writes, _source.Writes.Count);
        Assert.Single(_versions.Records);
    }

    [Fact]
    public async Task ATakenVersionWithNoSourceIsStillUsed()
    {
        var taken = TemplateVersionRecords.StorageSample(Template, Version);
        _versions.Seed(taken);

        // The class cannot be listed or read at all: the copy answers from the record.
        var operations = new OperationStore(new FailingStore());
        var outcome = await Take(reader: new StorageTemplateSource(operations, _options, new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance))
            .TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var again = Assert.IsType<AlreadyTakenVersion>(outcome);
        Assert.Equal(taken, again.Version);
        Assert.Empty(operations.Opened);
        Assert.Equal(0, operations.ListCalls);
        Assert.Empty(_source.Writes);
    }

    [Fact]
    public async Task AnotherConsumersTakenVersionIsAnsweredAsNotFoundWithoutReadingTheSource()
    {
        _versions.Seed(TemplateVersionRecords.StorageSample(Template, Version));
        var writes = _source.Writes.Count;

        var outcome = await Take().TakeAsync(Template, Version, OtherConsumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Equal(writes, _source.Writes.Count);
    }

    [Fact]
    public async Task AVersionWhoseSourceCannotBeReadRefusesRetryablyAndWritesNothing()
    {
        var take = new TemplateTake(
            new StorageTemplateSource(new FailingStore(), _options, new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance),
            _versions,
            _source.Store,
            new ManifestReader(),
            new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance),
            checks: [],
            new TemplateTakeMetrics(),
            _clock,
            Options.Create(_options),
            NullLogger<TemplateTake>.Instance);

        var outcome = await take.TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateSourceUnavailable, refused.Code);
        Assert.True(refused.Retryable);
        Assert.Contains("retried later", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_versions.Records);
    }

    [Fact]
    public async Task AVersionWithNoPublicationIsNotFound()
    {
        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.False(refused.Retryable);
    }

    [Fact]
    public async Task FilesWithoutARecordAreInvalidSayingNothingWasPublished()
    {
        await _source.WriteFileAsync(Template, Version, "template.yaml", Encoding.UTF8.GetBytes("manifestVersion: \"1.0\""));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Contains("Nothing was published", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionPublishedMoreThanOnceIsInvalidAndSaysSo()
    {
        await _source.PublishAsync(Template, Version, Package());
        await _source.WriteRecordAsync(
            Template,
            Version,
            Encoding.UTF8.GetBytes(
                """{"descriptorVersion":"1.0","templateId":"invoice-energy","version":"1.1.0","files":[{"path":"template.yaml","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"fileCount":1,"totalSize":1}"""));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Contains("published more than once", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordThisBuildDoesNotReadIsUnsupportedCapability()
    {
        await _source.WriteRecordAsync(
            Template,
            Version,
            Encoding.UTF8.GetBytes(
                """{"descriptorVersion":"2.0","templateId":"invoice-energy","version":"1.1.0","files":[],"fileCount":0,"totalSize":0}"""));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.UnsupportedCapability, refused.Code);
        Assert.False(refused.Retryable);
    }

    [Fact]
    public async Task ARecordThatDeclaresAnotherVersionIsInvalidNamingBoth()
    {
        await _source.WriteRecordAsync(
            Template,
            Version,
            Encoding.UTF8.GetBytes(
                """{"descriptorVersion":"1.0","templateId":"invoice-energy","version":"1.2.0","files":[{"path":"template.yaml","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"fileCount":1,"totalSize":1}"""));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Contains(Version, refused.Reason, StringComparison.Ordinal);
        Assert.Contains("1.2.0", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(_versions.Records);
    }

    [Fact]
    public async Task ARecordThatNamesAFileTheStoreDoesNotHoldIsInvalidNamingThePath()
    {
        await _source.PublishRecordOnlyAsync(
            Template,
            Version,
            [("template.yaml", 4, new string('a', 64)), ("schema.json", 2, new string('b', 64))]);

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Contains("template.yaml", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("template-source/", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlippedByteIsInvalidNamingThePath()
    {
        await _source.PublishAsync(Template, Version, Package());
        _source.Corrupt(TemplateSourceKeys.File(Template, Version, "schema.json"), Encoding.UTF8.GetBytes("{\"changed\":true}"));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Contains("schema.json", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheManifestIsReadFirstAndAForeignVersionAnswersNotFoundBeforeAnyContentRefusal()
    {
        await _source.PublishAsync(Template, Version, Package(owner: Consumer));
        var operations = new OperationStore(_source.Store);
        var take = Take(reader: new StorageTemplateSource(operations, _options, new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance));

        var outcome = await take.TakeAsync(Template, Version, OtherConsumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.DoesNotContain(operations.Opened, key => key.Contains("/schema.json", StringComparison.Ordinal));
        Assert.DoesNotContain(operations.Opened, key => key.Contains("/html/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheManifestVersionRuleNamesThePublicationAndWhatToFix()
    {
        await _source.PublishAsync(Template, Version, Package(manifestVersion: "1.2.1"));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Contains("publication found for version", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("1.2.1", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Publish the manifest", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionThatLosesTheInsertRaceBindsTheWinnersRecordAndWarns()
    {
        await _source.PublishAsync(Template, Version, Package());
        var winner = TemplateVersionRecords.StorageSample(Template, Version, packageSha256: new string('b', 64));
        var racing = new RaceOnInsertStore(_versions, winner);

        var outcome = await Take(store: racing).TakeAsync(Template, Version, Consumer, "job-7", TestContext.Current.CancellationToken);

        var bound = Assert.IsType<ConflictLoggedVersion>(outcome);
        Assert.Equal(winner, bound.Record);
        Assert.Single(_logs.Lines, line => line.Contains("TEMPLATE_VERSION_CONFLICT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIdenticalTakeThatWinsTheInsertIsTheCopyTheLoserUses()
    {
        await _source.PublishAsync(Template, Version, Package());
        var identical = new IdenticalInsertStore(_versions);

        var outcome = await Take(store: identical).TakeAsync(Template, Version, Consumer, "job-8", TestContext.Current.CancellationToken);

        // The store answered with the row an identical take had just committed: the caller's
        // own bytes, at the same key and with the same hash, both computed from the package
        // that was published. The job uses that copy, and the lost race is not a refusal.
        var taken = Assert.IsType<AlreadyTakenVersion>(outcome);
        Assert.NotNull(identical.Answered);
        Assert.Equal(identical.Answered, taken.Version);
        Assert.Equal(identical.Answered!.PackageSha256, taken.Version.PackageSha256);
        Assert.IsNotType<RefusedVersion>(outcome);
    }

    [Fact]
    public async Task AnInvalidNameIsNotFoundWithoutReadingTheSource()
    {
        var outcome = await Take().TakeAsync(Template, "not a version", Consumer, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.Empty(_source.Writes);
    }

    [Fact]
    public async Task ATakeWhoseInsertFailsAfterTheObjectWasWrittenConvergesOnOneRowAndOneKey()
    {
        await _source.PublishAsync(Template, Version, Package());
        var failing = new FirstInsertFailsStore(_versions);

        var first = await Take(store: failing).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        // The copy was written, and the record is what makes the version usable.
        var refused = Assert.IsType<RefusedVersion>(first);
        Assert.Equal(TemplateTakeCodes.ExecutionFailed, refused.Code);
        Assert.True(refused.Retryable);
        Assert.Empty(_versions.Records);
        var writtenKey = Assert.Single(await KeysAsync(TemplatePackageKeys.ClassPrefix));
        Assert.StartsWith($"templates/{Consumer}/{Template}/{Version}/", writtenKey, StringComparison.Ordinal);

        // The retry converges on one row and the key the copy already sits under.
        var record = AssertTaken(await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken));
        Assert.Equal(writtenKey, record.ObjectKey.ToString());
        Assert.Single(_versions.Records);
        Assert.Single(await KeysAsync(TemplatePackageKeys.ClassPrefix));

        var again = Assert.IsType<AlreadyTakenVersion>(
            await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken));
        Assert.Equal(record, again.Version);

        var read = await _source.Store.OpenReadAsync(record.ObjectKey, record.PackageSize, record.PackageSha256, TestContext.Current.CancellationToken);
        Assert.True(read.Succeeded, read.Error?.Message);
        await using (var verified = read.Value!)
        {
            await verified.Content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ARecordsFileOrderIsNormalisedToTheWritersPathOrder()
    {
        var manifest = Encoding.UTF8.GetBytes(ManifestText(Consumer, Version, schema: "schema.json"));
        var schema = Encoding.UTF8.GetBytes("{}");
        var extra = Encoding.UTF8.GetBytes("[]");
        await _source.WriteFileAsync(Template, Version, "template.yaml", manifest);
        await _source.WriteFileAsync(Template, Version, "schema.json", schema);
        await _source.WriteFileAsync(Template, Version, "a.json", extra);

        // The publisher's own order: the manifest first, the two others after it —
        // never the ordinal order the record is normalised to.
        await _source.WriteRecordAsync(Template, Version, Encoding.UTF8.GetBytes(
            $$"""
            {"descriptorVersion":"1.0","templateId":"{{Template}}","version":"{{Version}}","files":[
            {"path":"template.yaml","size":{{manifest.Length}},"sha256":"{{TemplateSourceClass.Sha256(manifest)}}"},
            {"path":"schema.json","size":{{schema.Length}},"sha256":"{{TemplateSourceClass.Sha256(schema)}}"},
            {"path":"a.json","size":{{extra.Length}},"sha256":"{{TemplateSourceClass.Sha256(extra)}}"}],
            "fileCount":3,"totalSize":{{manifest.Length + schema.Length + extra.Length}}}
            """));

        var record = AssertTaken(await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken));

        Assert.Equal(["a.json", "schema.json", "template.yaml"], record.SourceObjects!.Select(file => file.Path));
        Assert.Equal(3, record.FileCount);
    }

    [Fact]
    public async Task AVersionWithBuildMetadataIsTakenAndItsKeySegmentIsEncoded()
    {
        const string Build = "1.0.0+build.1";
        await _source.PublishAsync(Template, Build, Package(manifestVersion: Build));

        var record = AssertTaken(await Take().TakeAsync(Template, Build, Consumer, null, TestContext.Current.CancellationToken));

        Assert.Equal(Build, record.Version);
        Assert.StartsWith("h-", record.ObjectKey.Segments[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemplateIdAtAndPastTheRawSegmentBoundaryKeepsItsKey()
    {
        var raw = new string('a', KeySegmentEncoding.MaxRawLength);
        var encoded = new string('a', KeySegmentEncoding.MaxRawLength + 1);
        await _source.PublishAsync(raw, Version, Package(templateId: raw));
        await _source.PublishAsync(encoded, Version, Package(templateId: encoded));

        var rawRecord = AssertTaken(await Take().TakeAsync(raw, Version, Consumer, null, TestContext.Current.CancellationToken));
        var encodedRecord = AssertTaken(await Take().TakeAsync(encoded, Version, Consumer, null, TestContext.Current.CancellationToken));

        Assert.Equal(raw, rawRecord.ObjectKey.Segments[2]);
        Assert.StartsWith(KeySegmentEncoding.HashedPrefix, encodedRecord.ObjectKey.Segments[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageWhoseOnlyFileIsTheManifestIsRefusedNamingTheSchema()
    {
        var manifest = Package()["template.yaml"];
        await _source.PublishAsync(
            Template,
            Version,
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["template.yaml"] = manifest });

        var refused = Assert.IsType<RefusedVersion>(
            await Take().TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken));

        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Contains("schema", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_versions.Records);
    }

    private async Task<IReadOnlyList<string>> KeysAsync(string prefix)
    {
        var keys = new List<string>();
        await foreach (var info in _source.Store.ListAsync(prefix, TestContext.Current.CancellationToken))
        {
            keys.Add(info.Key.ToString());
        }

        return keys;
    }

    private TemplateTake Take(
        ITemplateVersionStore? store = null,
        StorageTemplateSource? reader = null) =>
        new(
            reader ?? _source.Reader(_options),
            store ?? _versions,
            _source.Store,
            new ManifestReader(),
            new TemplateVersionConflict(_loggerFactory.CreateLogger(TemplateVersionConflict.LogCategory)),
            _checks,
            new TemplateTakeMetrics(),
            _clock,
            Options.Create(_options),
            _loggerFactory.CreateLogger<TemplateTake>());

    private static TemplateVersionRecord AssertTaken(TemplateTakeOutcome outcome)
    {
        var taken = Assert.IsType<TakenVersion>(outcome);
        return taken.Version;
    }

    private static Dictionary<string, byte[]> Package(
        string owner = Consumer,
        string manifestVersion = Version,
        string templateId = Template) => new(StringComparer.Ordinal)
    {
        ["template.yaml"] = Encoding.UTF8.GetBytes(ManifestText(owner, manifestVersion, "schema.json", templateId)),
        ["schema.json"] = Encoding.UTF8.GetBytes("{}"),
        ["html/document-start.html.sbn"] = Encoding.UTF8.GetBytes("<html><body>"),
        ["html/row.html.sbn"] = Encoding.UTF8.GetBytes("<tr></tr>"),
        ["html/document-end.html.sbn"] = Encoding.UTF8.GetBytes("</body></html>"),
    };

    private static string ManifestText(
        string owner,
        string manifestVersion,
        string schema,
        string templateId = Template) =>
        $"""
        manifestVersion: "1.0"
        id: {templateId}
        version: "{manifestVersion}"
        owner: {owner}
        shared: false
        engine: scriban
        engineVersion: "7.5.1"
        dataContract:
          version: "1.0"
          profile: header-items-v1
          schema: {schema}
        outputs:
          html:
            parts:
              documentStart: html/document-start.html.sbn
              row: html/row.html.sbn
              documentEnd: html/document-end.html.sbn
        """;

    /// <summary>A store that records every object the take opened or listed, in order.</summary>
    private sealed class OperationStore(IObjectStore inner) : IObjectStore
    {
        private readonly List<string> _opened = [];
        private int _listCalls;

        public IReadOnlyList<string> Opened
        {
            get
            {
                lock (_opened)
                {
                    return [.. _opened];
                }
            }
        }

        /// <summary>How many listings reached the store; a take that answers from a record never lists.</summary>
        public int ListCalls => Volatile.Read(ref _listCalls);

        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            inner.WriteAsync(key, content, cancellationToken);

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken)
        {
            lock (_opened)
            {
                _opened.Add(key.ToString());
            }

            return inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);
        }

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetInfoAsync(key, cancellationToken);

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, cancellationToken);

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _listCalls);
            return inner.ListAsync(classPrefix, cancellationToken);
        }
    }

    /// <summary>A store that answers the outage the take keeps apart from absence.</summary>
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
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        private static StorageException Failure() =>
            new(StorageOutcome.Transient, new StorageError("storage.transient", "The object store did not answer."));
    }

    /// <summary>A store whose first insert does not answer, as an outage does once, and answers after it.</summary>
    private sealed class FirstInsertFailsStore(ITemplateVersionStore inner) : ITemplateVersionStore
    {
        private bool _failed;

        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            inner.FindAsync(templateId, version, cancellationToken);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            inner.ListByTemplateAsync(templateId, cancellationToken);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                throw new TemplateStoreUnavailableException("the insert did not answer", new TimeoutException());
            }

            return inner.TryInsertAsync(record, cancellationToken);
        }
    }

    /// <summary>A store whose insert meets the record another take committed first, with other bytes.</summary>
    private sealed class RaceOnInsertStore(ITemplateVersionStore inner, TemplateVersionRecord winner) : ITemplateVersionStore
    {
        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            inner.FindAsync(templateId, version, cancellationToken);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            inner.ListByTemplateAsync(templateId, cancellationToken);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken)
        {
            inner.TryInsertAsync(winner, cancellationToken).GetAwaiter().GetResult();
            return inner.TryInsertAsync(record, cancellationToken);
        }
    }

    /// <summary>A store whose insert meets the identical record another take committed a microsecond earlier.</summary>
    private sealed class IdenticalInsertStore(ITemplateVersionStore inner) : ITemplateVersionStore
    {
        public TemplateVersionRecord? Answered { get; private set; }

        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            inner.FindAsync(templateId, version, cancellationToken);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            inner.ListByTemplateAsync(templateId, cancellationToken);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken)
        {
            Answered = record;
            return Task.FromResult(TemplateVersionInsert.Existing(record));
        }
    }
}

