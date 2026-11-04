using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ManifestReader = ReportService.TemplateSource.Manifest.TemplateManifestReader;

namespace ReportService.TemplateSource.Tests.Take;

/// <summary>
/// The take, at service level: one version read from the source at one commit,
/// kept as one write-once object under the content-addressed key and made usable
/// by an insert-once record. The cases are the acceptance criteria of the
/// version-taking story: the first take and its record; a taken version that is
/// never read again; a taken version that changed in the branch, which is logged
/// and never taken; a source that cannot be reached; a folder that is not the
/// manifest's version; a commit that fails after the object was written; the
/// structural refusals, none of which names a package path; another consumer's
/// version, answered exactly like an absent one, before any content refusal; the
/// scratch directory each take was given, gone once the take returns; and a
/// truncated tree, which is never "not found".
///
/// A take makes its scratch area under the temporary directory the whole process
/// shares, so a case observes the directory its own take was given and never the
/// root the other takes write to.
/// </summary>
public sealed class TemplateTakeTests : IDisposable
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

    private readonly InMemoryTemplateSource _source = new();

    private readonly InMemoryTemplateVersionStore _versions = new();

    private readonly LogCapture _logs = new();

    private readonly ILoggerFactory _loggerFactory;

    private readonly List<ITemplateVersionCheck> _checks = [];

    private readonly FilesystemObjectStore _objects;

    private readonly TemplateSourceOptions _options = new()
    {
        Repository = "report-service/templates",
        Branch = "main",
        AllowInsecureLoopback = true,
    };

    private TemplateTake? _take;

    public TemplateTakeTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_root);
        _objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(_root, "storage"), CreateIfMissing = true },
            Path.Combine(_root, "storage"),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        _source.SeedPackage(Template, Version, Package(Manifest()));
    }

    public void Dispose()
    {
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
        var outcome = await Take().TakeAsync(Template, Version, Consumer, commit: null, reportJobId: "job-1", CancellationToken.None);

        var record = AssertTaken(outcome);
        Assert.Equal(Template, record.TemplateId);
        Assert.Equal(Version, record.Version);
        Assert.Equal(Consumer, record.OwnerConsumerId);
        Assert.False(record.Shared);
        Assert.Equal(_source.CommitSha, record.CommitSha);
        Assert.Equal(TemplateEngine.Scriban, record.Engine);
        Assert.Equal(TemplateEngineCatalog.ScribanVersion, record.EngineVersion);
        Assert.Equal("schema.json", record.SchemaRef);
        Assert.Equal(3, record.FileCount);
        Assert.Equal(_clock.Now, record.TakenAt);
        Assert.Single(_versions.Records);

        // The key is the content-addressed one, and the object reads back verified.
        Assert.Equal($"templates/{Consumer}/{Template}/{Version}/{record.PackageSha256}", record.ObjectKey.ToString());
        var read = await _objects.OpenReadAsync(record.ObjectKey, record.PackageSize, record.PackageSha256, CancellationToken.None);
        Assert.True(read.Succeeded, read.Error?.Message);
        await using (var verified = read.Value!)
        {
            await verified.Content.CopyToAsync(Stream.Null, CancellationToken.None);
        }

        // The manifest was read once and stored as JSON with the record, which no later reader re-parses.
        Assert.StartsWith("""{"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0","owner":"billing-system","shared":false,"engine":"scriban","engineVersion":"7.5.1","dataContract":""", record.ManifestJson, StringComparison.Ordinal);
        Assert.Equal(record.ManifestJson, TemplateManifestJson.Write(TemplateManifestJson.Parse(record.ManifestJson)));
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("version 1.1.0", StringComparison.Ordinal) && line.Contains("ended taken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATakenVersionIsUsedAgainWithoutReadingTheSource()
    {
        await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);
        _source.SetUnreachable(true);
        var requestsBefore = _source.RequestCount;

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        Assert.IsType<AlreadyTakenVersion>(outcome);
        Assert.Equal(requestsBefore, _source.RequestCount);
        Assert.Single(_versions.Records);
    }

    [Fact]
    public async Task ATakenVersionChangedInTheBranchIsLoggedAndTheCopyIsKept()
    {
        var taken = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);
        var record = AssertTaken(taken);

        // The branch changed under the same version; the next take reads it again only
        // because it started before the first take committed, which is the race below.
        _source.Replace(Template, Version, Package(Manifest() + "changed: true\n"));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        Assert.IsType<AlreadyTakenVersion>(outcome);
        Assert.Equal(record, Assert.Single(_versions.Records));
    }

    [Fact]
    public async Task ATakeThatLostTheRaceBindsTheWinnersRecordAndWarnsWithBothHashes()
    {
        // A first take committed while this one was reading; the branch changed between
        // the two reads, so the bytes this one read differ from the taken copy.
        var winner = TemplateVersionRecords.Sample(Template, Version, Consumer, packageSha256: new string('a', 64));
        var racing = new RaceOnReadStore(_versions, winner);

        var outcome = await Take(store: racing).TakeAsync(Template, Version, Consumer, null, "job-7", CancellationToken.None);

        var bound = Assert.IsType<ConflictLoggedVersion>(outcome);
        Assert.Equal(winner, bound.Record);
        Assert.Equal(winner, Assert.Single(racing.Records));
        var warning = Assert.Single(_logs.Lines, line => line.Contains("TEMPLATE_VERSION_CONFLICT", StringComparison.Ordinal));
        Assert.Contains(winner.PackageSha256, warning, StringComparison.Ordinal);
        Assert.Contains("job-7", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATakeThatLosesTheInsertRaceBindsTheWinnersRecordAndWarns()
    {
        var winner = TemplateVersionRecords.Sample(Template, Version, Consumer, packageSha256: new string('b', 64));
        var racing = new RaceOnInsertStore(_versions, winner);
        var take = Take(store: racing);

        var outcome = await take.TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        Assert.IsType<ConflictLoggedVersion>(outcome);
        Assert.Equal(winner, Assert.Single(racing.Records));
        Assert.Single(_logs.Lines, line => line.Contains("TEMPLATE_VERSION_CONFLICT", StringComparison.Ordinal));

        // Every later take returns the record without reading the source again.
        var requests = _source.RequestCount;
        Assert.IsType<AlreadyTakenVersion>(
            await take.TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None));
        Assert.Equal(requests, _source.RequestCount);
    }

    [Fact]
    public async Task AnotherConsumersTakenVersionIsAnsweredAsNotFoundWithoutReadingTheSource()
    {
        _versions.Seed(TemplateVersionRecords.Sample(Template, Version, Consumer));
        var requests = _source.RequestCount;

        var outcome = await Take().TakeAsync(Template, Version, OtherConsumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Equal(requests, _source.RequestCount);
    }

    [Fact]
    public async Task ATakenVersionWithAnUnreachableSourceIsStillUsed()
    {
        await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);
        _source.SetUnreachable(true);

        Assert.IsType<AlreadyTakenVersion>(
            await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AVersionsSourceThatCannotBeReachedRefusesRetryablyAndWritesNothing(bool unreachable)
    {
        if (unreachable)
        {
            _source.SetUnreachable(true);
        }
        else
        {
            _source.SetMisconfigured(true);
        }

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateSourceUnavailable, refused.Code);
        Assert.True(refused.Retryable);
        Assert.Contains("retried later", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_versions.Records);
        Assert.Empty(Objects());
    }

    [Fact]
    public async Task AFolderThatIsNotTheManifestsVersionIsRefusedNamingBoth()
    {
        _source.SeedPackage(Template, Version, Package(Manifest(version: "1.2.1")));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Contains(Version, refused.Reason, StringComparison.Ordinal);
        Assert.Contains("1.2.1", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("Rename the folder", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(_versions.Records);
        Assert.Empty(Objects());

        // The version is read from templates/<id>/<version>/ and from nowhere else.
        Assert.All(_source.RequestLog, request => Assert.True(request.Version is null or Version, request.Version));
    }

    [Fact]
    public async Task AVersionThatDoesNotExistIsNotFound()
    {
        var outcome = await Take().TakeAsync(Template, "9.9.9", Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Empty(Objects());
    }

    [Fact]
    public async Task AnInputThatCannotNameAVersionIsNotFoundWithoutReachingTheStoreOrTheSource()
    {
        var invalid = new[]
        {
            ("Invoice-Energy", Version),
            (Template, "latest"),
            (Template, "1.0"),
            (new string('a', 129), Version),
        };

        foreach (var (templateId, version) in invalid)
        {
            var outcome = await Take().TakeAsync(templateId, version, Consumer, null, null, CancellationToken.None);

            Assert.Equal(TemplateTakeCodes.TemplateNotFound, Assert.IsType<RefusedVersion>(outcome).Code);
        }

        Assert.Equal(0, _versions.FindCalls);
        Assert.Equal(0, _source.RequestCount);
    }

    [Theory]
    [InlineData("missing-owner")]
    [InlineData("invalid-owner")]
    [InlineData("unreadable")]
    public async Task AVersionWhoseOwnerCannotBeReadIsInvalidWithTheFileTheLineAndTheRule(string owner)
    {
        _source.SeedPackage(Template, Version, Package(owner switch
        {
            "missing-owner" => Manifest(owner: null),
            "invalid-owner" => Manifest(owner: "Billing System"),
            _ => "id: [unclosed\n",
        }));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Contains("template.yaml", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(_versions.Records);
        Assert.Empty(Objects());
    }

    [Fact]
    public async Task AVersionsOwnerThatIsAnotherConsumerIsNotFoundBeforeAnyContentRefusal()
    {
        // Another consumer's untaken invalid version: the engine is one this deployment
        // does not render and the schema names a file the package does not hold.
        _source.SeedPackage(
            Template,
            Version,
            Package(Manifest(owner: Consumer, engine: "razor", schema: "absent.json")));
        _checks.Add(new RecordingCheck(TemplateCheckResult.Refuse(TemplateCheckCodes.TemplateInvalid, "a check refused it")));

        var outcome = await Take().TakeAsync(Template, Version, OtherConsumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.False(refused.Retryable);
        Assert.DoesNotContain("razor", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("absent.json", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("engine", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_versions.Records);
        Assert.Empty(Objects());
        Assert.Equal(0, ((RecordingCheck)_checks[0]).Calls);
    }

    [Fact]
    public async Task TheSameVersionReadForItsOwnerReachesTheContentRefusals()
    {
        _source.SeedPackage(Template, Version, Package(Manifest(engine: "razor")));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.UnsupportedCapability, refused.Code);
        Assert.Contains("engine", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Objects());
    }

    [Fact]
    public async Task ASharedVersionAdmitsAnotherConsumerToTheContentRefusals()
    {
        _source.SeedPackage(Template, Version, Package(Manifest(owner: Consumer, shared: true, engine: "razor")));

        var outcome = await Take().TakeAsync(Template, Version, OtherConsumer, null, null, CancellationToken.None);

        Assert.Equal(
            TemplateTakeCodes.UnsupportedCapability,
            Assert.IsType<RefusedVersion>(outcome).Code);
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("truncated-walk")]
    [InlineData("truncated-folder")]
    [InlineData("too-many-files")]
    [InlineData("oversized-file")]
    [InlineData("too-many-bytes")]
    [InlineData("long-segment")]
    [InlineData("long-path")]
    public async Task AReadTheSourceRefusesIsInvalidNamingTheKindOrTheCapAndNoPath(string kind)
    {
        var take = Take(source: FailingSource.For(_source, kind));

        var outcome = await take.TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.False(refused.Retryable);
        Assert.DoesNotContain("html/", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("schema.json", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("template.yaml", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(_versions.Records);
        Assert.Empty(Objects());
    }

    [Fact]
    public async Task ATruncatedTreeIsNeverNotFound()
    {
        var take = Take(source: FailingSource.For(_source, "truncated-walk"));

        var outcome = await take.TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.NotEqual(TemplateTakeCodes.TemplateNotFound, refused.Code);
        Assert.Contains("more entries than the source will list in one answer", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommitThatFailsAfterTheObjectWasWrittenLeavesTheVersionUntakenAndTheRetryConverges()
    {
        var failing = new FailFirstInsertStore(_versions);
        var take = Take(store: failing);

        var outcome = await take.TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.ExecutionFailed, refused.Code);
        Assert.True(refused.Retryable);
        Assert.Empty(_versions.Records);

        var retried = await take.TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var record = AssertTaken(retried);
        Assert.Single(_versions.Records);
        Assert.Equal($"templates/{Consumer}/{Template}/{Version}/{record.PackageSha256}", record.ObjectKey.ToString());
        Assert.Single(Objects());
    }

    [Fact]
    public async Task APinnedCommitIsUsedAndNoCommitRequestIsMade()
    {
        var older = new SourceCommit(new string('c', 40), new string('d', 40));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, older, null, CancellationToken.None);

        var record = AssertTaken(outcome);
        Assert.Equal(older.CommitSha, record.CommitSha);
        Assert.DoesNotContain("commit", _source.Steps);
    }

    [Fact]
    public async Task ARefusingCheckEndsTheTakeAndNothingIsWritten()
    {
        _checks.Add(new RecordingCheck(TemplateCheckResult.Refuse(TemplateCheckCodes.TemplateInvalid, "the sample does not match the schema")));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, refused.Code);
        Assert.Equal("the sample does not match the schema", refused.Reason);
        Assert.False(refused.Retryable);
        Assert.Empty(_versions.Records);
        Assert.Empty(Objects());
    }

    [Fact]
    public async Task ACheckNeverRunsForAConsumerThatMayNotUseTheVersion()
    {
        _versions.Seed(TemplateVersionRecords.Sample(Template, Version, Consumer));
        _checks.Add(new RecordingCheck(TemplateCheckResult.Pass()));

        await Take().TakeAsync(Template, Version, OtherConsumer, null, null, CancellationToken.None);

        Assert.Equal(0, ((RecordingCheck)_checks[0]).Calls);
    }

    [Fact]
    public async Task TheScratchDirectoryIsDeletedOnEveryPath()
    {
        var scratch = new ScratchDirectories(refusedVersion: "1.2.0");
        _checks.Add(scratch);
        _source.SeedPackage(Template, "1.2.0", Package(Manifest(version: "1.2.0")));

        AssertTaken(await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None));

        var refused = await Take().TakeAsync(Template, "1.2.0", Consumer, null, null, CancellationToken.None);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, Assert.IsType<RefusedVersion>(refused).Code);

        Assert.Equal(2, scratch.Directories.Count);
        Assert.All(scratch.Directories, seen => Assert.True(
            seen.Existed,
            $"the take had no scratch directory at {seen.Path}"));
        Assert.All(scratch.Directories, seen => Assert.False(
            Directory.Exists(seen.Path),
            $"the take left its scratch directory behind: {seen.Path}"));
    }

    [Theory]
    [InlineData("1.0.0+build.1")]
    [InlineData("2.0.0-RC.1")]
    public async Task AVersionOutsideTheRawKeyGrammarGetsAHashedSegment(string version)
    {
        _source.SeedPackage(Template, version, Package(Manifest(version: version)));

        var outcome = await Take().TakeAsync(Template, version, Consumer, null, null, CancellationToken.None);

        var record = AssertTaken(outcome);
        Assert.Contains("/h-", record.ObjectKey.ToString(), StringComparison.Ordinal);
        Assert.Equal(_versions.Records.Single(), record);
    }

    [Fact]
    public async Task ATemplateIdentifierThatIsLegalAndLongEnoughToBeHashedIsTaken()
    {
        var identifier = "template-" + new string('a', 24);
        Assert.Equal(33, identifier.Length);
        _source.SeedPackage(identifier, Version, Package(Manifest(id: identifier)));

        var outcome = await Take().TakeAsync(identifier, Version, Consumer, null, null, CancellationToken.None);

        var record = AssertTaken(outcome);
        Assert.Contains("/h-", record.ObjectKey.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionWhoseManifestIsOverSixtyFourKilobytesIsInvalid()
    {
        var padded = Manifest() + "#" + new string('x', TemplateManifestReader.MaxBytes);
        _source.SeedPackage(Template, Version, Package(padded));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        Assert.Equal(TemplateTakeCodes.TemplateInvalid, Assert.IsType<RefusedVersion>(outcome).Code);
        Assert.Empty(Objects());
    }

    [Fact]
    public async Task AZeroByteFileIsTaken()
    {
        var files = Package();
        files.Add("assets/empty.bin", []);
        _source.SeedPackage(Template, Version, files);

        var outcome = await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None);

        Assert.Equal(4, AssertTaken(outcome).FileCount);
    }

    [Fact]
    public async Task AFileExactlyAtTheConfiguredLimitIsTakenAndOneByteMoreIsRefused()
    {
        var inside = Package();
        inside.Add("assets/exact.bin", new byte[_options.MaxFileBytes]);
        _source.SeedPackage(Template, Version, inside);

        AssertTaken(
            await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None));

        var outside = Package();
        outside.Add("assets/over.bin", new byte[_options.MaxFileBytes + 1]);
        _source.SeedPackage(Template, "1.3.0", outside);
        var outcome = await Take().TakeAsync(Template, "1.3.0", Consumer, null, null, CancellationToken.None);

        Assert.Equal(TemplateTakeCodes.TemplateInvalid, Assert.IsType<RefusedVersion>(outcome).Code);
    }

    [Fact]
    public async Task TheFileCountLimitIsInclusive()
    {
        var atLimit = Package();
        for (var index = 3; index < _options.MaxFiles; index++)
        {
            atLimit.Add($"assets/{index}.bin", [1]);
        }

        _source.SeedPackage(Template, Version, atLimit);
        AssertTaken(
            await Take().TakeAsync(Template, Version, Consumer, null, null, CancellationToken.None));

        var overLimit = Package();
        for (var index = 3; index <= _options.MaxFiles; index++)
        {
            overLimit.Add($"assets/{index}.bin", [1]);
        }

        _source.SeedPackage(Template, "1.4.0", overLimit);
        var outcome = await Take().TakeAsync(Template, "1.4.0", Consumer, null, null, CancellationToken.None);

        Assert.Equal(TemplateTakeCodes.TemplateInvalid, Assert.IsType<RefusedVersion>(outcome).Code);
    }

    /// <summary>The take produced a record; a refusal is reported with the text the author would see.</summary>
    private static TemplateVersionRecord AssertTaken(TemplateTakeOutcome outcome) =>
        outcome is TakenVersion taken
            ? taken.Version
            : throw new Xunit.Sdk.XunitException(
                $"expected a taken version, and the take answered {outcome.Name} {(outcome as RefusedVersion)?.Code}: {(outcome as RefusedVersion)?.Reason}");

    private TemplateTake Take(ITemplateVersionStore? store = null, ITemplateSource? source = null) =>
        _take ??= new TemplateTake(
            source ?? _source,
            store ?? _versions,
            _objects,
            new ManifestReader(),
            new TemplateVersionConflict(_loggerFactory.CreateLogger(TemplateVersionConflict.LogCategory)),
            _checks,
            new TemplateTakeMetrics(),
            _clock,
            Options.Create(_options),
            _loggerFactory.CreateLogger<TemplateTake>());

    private static Dictionary<string, byte[]> Package(string? manifest = null) => new(StringComparer.Ordinal)
    {
        ["template.yaml"] = Encoding.UTF8.GetBytes(manifest ?? Manifest()),
        ["schema.json"] = Encoding.UTF8.GetBytes("{}"),
        ["html/row.html.sbn"] = Encoding.UTF8.GetBytes("<tr></tr>"),
    };

    private static string Manifest(
        string id = Template,
        string version = Version,
        string? owner = Consumer,
        bool shared = false,
        string engine = "scriban",
        string schema = "schema.json") =>
        $"""
        manifestVersion: "1.0"
        id: {id}
        version: "{version}"
        {(owner is null ? string.Empty : "owner: " + owner)}
        shared: {(shared ? "true" : "false")}
        engine: {engine}
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
            styles: [html/styles.css]
        """;

    private IEnumerable<string> Objects() =>
        Directory.Exists(Path.Combine(_root, "storage"))
            ? Directory.GetFiles(Path.Combine(_root, "storage"), "*", SearchOption.AllDirectories)
            : [];

    /// <summary>A check that records how often it ran and answers what the case fixed.</summary>
    private sealed class RecordingCheck(TemplateCheckResult answer) : ITemplateVersionCheck
    {
        public int Calls { get; private set; }

        public string Name => "recording";

        public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    /// <summary>A check that records the scratch directory of every take it is
    /// given, and refuses the one version it is told to refuse.</summary>
    private sealed class ScratchDirectories(string? refusedVersion) : ITemplateVersionCheck
    {
        private readonly List<(string Path, bool Existed)> _directories = [];

        public IReadOnlyList<(string Path, bool Existed)> Directories => _directories;

        public string Name => "scratch-directories";

        public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
        {
            _directories.Add((candidate.PackageDirectory, Directory.Exists(candidate.PackageDirectory)));
            return Task.FromResult(
                string.Equals(candidate.Version, refusedVersion, StringComparison.Ordinal)
                    ? TemplateCheckResult.Refuse(TemplateCheckCodes.TemplateInvalid, "the scratch observation refused this version")
                    : TemplateCheckResult.Pass());
        }
    }

    /// <summary>A store that records the winner of a race the moment this take tries to commit.</summary>
    private sealed class RaceOnInsertStore(InMemoryTemplateVersionStore inner, TemplateVersionRecord winner) : ITemplateVersionStore
    {
        public IReadOnlyList<TemplateVersionRecord> Records => inner.Records;

        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            inner.FindAsync(templateId, version, cancellationToken);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            inner.ListByTemplateAsync(templateId, cancellationToken);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken)
        {
            inner.Seed(winner);
            return inner.TryInsertAsync(record, cancellationToken);
        }
    }

    /// <summary>A store that answers the second look with the record another take committed in between.</summary>
    private sealed class RaceOnReadStore(InMemoryTemplateVersionStore inner, TemplateVersionRecord winner) : ITemplateVersionStore
    {
        private int _finds;

        public IReadOnlyList<TemplateVersionRecord> Records => inner.Records;

        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _finds) == 1)
            {
                return Task.FromResult<TemplateVersionRecord?>(null);
            }

            inner.Seed(winner);
            return inner.FindAsync(templateId, version, cancellationToken);
        }

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            inner.ListByTemplateAsync(templateId, cancellationToken);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) =>
            inner.TryInsertAsync(record, cancellationToken);
    }

    /// <summary>A store whose first insert does not answer, as a database that went away.</summary>
    private sealed class FailFirstInsertStore(InMemoryTemplateVersionStore inner) : ITemplateVersionStore
    {
        private bool _failed;

        public IReadOnlyList<TemplateVersionRecord> Records => inner.Records;

        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            inner.FindAsync(templateId, version, cancellationToken);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            inner.ListByTemplateAsync(templateId, cancellationToken);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                throw new TemplateStoreUnavailableException(new TimeoutException("the store did not answer"));
            }

            return inner.TryInsertAsync(record, cancellationToken);
        }
    }

    /// <summary>A source that refuses one step the way the configured source does.</summary>
    private sealed class FailingSource(InMemoryTemplateSource inner, TemplateSourceFailure? failure, string? step) : ITemplateSource
    {
        public static ITemplateSource For(InMemoryTemplateSource inner, string kind) => kind switch
        {
            "symlink" => new FailingSource(inner, TemplateSourceFailure.UnsupportedEntry, "list"),
            "truncated-walk" => new FailingSource(inner, TemplateSourceFailure.PackageTooLarge, "tree"),
            "truncated-folder" => new FailingSource(inner, TemplateSourceFailure.PackageTooLarge, "list"),
            _ => new ShapeSource(inner, kind),
        };

        public Task<SourceCommit> ResolveBranchAsync(CancellationToken cancellationToken) =>
            inner.ResolveBranchAsync(cancellationToken);

        public Task<SourceFolder?> FindVersionFolderAsync(SourceCommit commit, string templateId, string version, CancellationToken cancellationToken) =>
            step == "tree" ? throw Refused() : inner.FindVersionFolderAsync(commit, templateId, version, cancellationToken);

        public Task<SourceTemplateVersions?> ListVersionsAsync(SourceCommit commit, string templateId, CancellationToken cancellationToken) =>
            inner.ListVersionsAsync(commit, templateId, cancellationToken);

        public Task<IReadOnlyList<SourceFile>> ListFilesAsync(SourceFolder folder, CancellationToken cancellationToken) =>
            step == "list" ? throw Refused() : inner.ListFilesAsync(folder, cancellationToken);

        public Task OpenFileAsync(SourceFile file, Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken) =>
            inner.OpenFileAsync(file, consume, cancellationToken);

        // The client's own message names the entry; the take must not pass it on.
        private TemplateSourceException Refused() => failure switch
        {
            TemplateSourceFailure.PackageTooLarge => new TemplateSourceException(
                TemplateSourceFailure.PackageTooLarge,
                "The folder 'templates/invoice-energy/1.1.0/' holds more entries than the source will list in one answer."),
            _ => new TemplateSourceException(
                TemplateSourceFailure.UnsupportedEntry,
                "the package holds a symbolic link at 'html/link.html.sbn'"),
        };
    }

    /// <summary>A source that answers the same version with a package of an unusual shape.</summary>
    private sealed class ShapeSource(InMemoryTemplateSource inner, string kind) : ITemplateSource
    {
        public Task<SourceCommit> ResolveBranchAsync(CancellationToken cancellationToken) =>
            inner.ResolveBranchAsync(cancellationToken);

        public Task<SourceFolder?> FindVersionFolderAsync(SourceCommit commit, string templateId, string version, CancellationToken cancellationToken) =>
            inner.FindVersionFolderAsync(commit, templateId, version, cancellationToken);

        public Task<SourceTemplateVersions?> ListVersionsAsync(SourceCommit commit, string templateId, CancellationToken cancellationToken) =>
            inner.ListVersionsAsync(commit, templateId, cancellationToken);

        public Task<IReadOnlyList<SourceFile>> ListFilesAsync(SourceFolder folder, CancellationToken cancellationToken)
        {
            var files = inner.ListFilesAsync(folder, cancellationToken).GetAwaiter().GetResult();
            IReadOnlyList<SourceFile> shaped = kind switch
            {
                "too-many-files" => [.. Enumerable.Range(0, 257).Select(index => new SourceFile($"a/{index}.bin", Sha(index), 1))],
                "oversized-file" => [.. files, new SourceFile("html/oversized.bin", Sha(900), 8 * 1024 * 1024)],
                "too-many-bytes" => [.. files, new SourceFile("html/large.bin", Sha(901), 15 * 1024 * 1024)],
                "long-segment" => [.. files, new SourceFile($"html/{new string('a', 101)}.bin", Sha(902), 1)],
                "long-path" => [.. files, new SourceFile($"html/{string.Join('/', Enumerable.Repeat(new string('b', 60), 5))}", Sha(903), 1)],
                _ => throw new InvalidOperationException($"no shape named '{kind}'"),
            };
            return Task.FromResult(shaped);
        }

        public async Task OpenFileAsync(SourceFile file, Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken)
        {
            if (file.Path is not ("template.yaml" or "schema.json" or "html/row.html.sbn"))
            {
                await using var content = new MemoryStream(new byte[checked((int)file.Size)], writable: false);
                await consume(content, cancellationToken).ConfigureAwait(false);
                return;
            }

            await inner.OpenFileAsync(file, consume, cancellationToken).ConfigureAwait(false);
        }

        private static string Sha(int seed) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([(byte)seed, (byte)(seed >> 8)]));
    }
}
