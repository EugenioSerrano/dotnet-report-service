using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Data;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.Checks;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateChecks;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ManifestReader = ReportService.TemplateSource.Manifest.TemplateManifestReader;

namespace ReportService.TemplateSource.Tests.Take;

/// <summary>
/// The at-bound gate at the take: the delivered <see cref="TemplateTake"/> over the
/// template-source double, with the twelfth check and the thirteenth check shipped beside
/// each other - the sample render and the at-bound sample - and the sample port answered
/// by the in-memory renderer. The control is taken with its record, its object and the
/// SHA-256 read back; the clipped and the anchored-truncated variants are refused with
/// the take's own code and a message naming the part and the position; the unanchored
/// truncating twin is taken, which is the admitted residual asserted rather than assumed;
/// a render that could not run is raised as retryable and never turned into a refusal;
/// and a check that runs before the gate spends no at-bound render.
/// <para>
/// The derivation itself, the position walk and the shape controls' readings are held by
/// the check project's own cases; this class holds the take's behaviour over them: what is
/// written, what is refused, what is answered again, and what the gate's observation says.
/// </para>
/// </summary>
public sealed class AtBoundSampleTakeTests : IAsyncDisposable
{
    private const string Consumer = "billing-system";

    private const string Template = AtBoundFixtures.TemplateId;

    private const string Version = AtBoundFixtures.Version;

    private const int Bound = PdfRowBound.Bytes;

    /// <summary>The source and manifest read's planned bound, in seconds.</summary>
    private const int SourceAndManifestPlanSeconds = 30;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-at-bound-take",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));

    private readonly InMemoryTemplateVersionStore _versions = new();

    private readonly LogCapture _logs = new();

    private readonly ILoggerFactory _loggerFactory;

    private readonly TemplateSourceClass _source;

    private readonly TemplateSourceOptions _options = new();

    public AtBoundSampleTakeTests()
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
    public async Task TheControlIsTakenWithItsRecordObjectAndHashAndTheGateRanOnTheAnchoredPosition()
    {
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        await PublishAsync(AtBoundFixtures.Package());

        var record = AssertTaken(await Take(renderer).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken));

        Assert.Equal(Template, record.TemplateId);
        Assert.Equal(Version, record.Version);
        Assert.Equal(Consumer, record.OwnerConsumerId);
        Assert.Equal(64, record.PackageSha256!.Length);
        Assert.Equal($"templates/{Consumer}/{Template}/{Version}/{record.PackageSha256}", record.ObjectKey.ToString());

        // The object is kept and reads back verified against the record's size and hash.
        var read = await _source.Store.OpenReadAsync(record.ObjectKey, record.PackageSize, record.PackageSha256, TestContext.Current.CancellationToken);
        Assert.True(read.Succeeded, read.Error?.Message);
        await using (var verified = read.Value!)
        {
            await verified.Content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        }

        // The port was asked three times - the sample, the empty sample and the at-bound
        // sample - and the third request carries the record at exactly the bound.
        Assert.Equal(3, renderer.Requests.Count);
        Assert.False(renderer.Requests[0].EmptySample);
        Assert.True(renderer.Requests[1].EmptySample);
        var atBound = JsonNode.Parse(Encoding.UTF8.GetString(renderer.Requests[2].SampleJson.Span))!.AsObject();
        var value = atBound["items"]!.AsArray()[0]!["description"]!.GetValue<string>();
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(value));

        Assert.Contains(
            _logs.Lines,
            line => line.Contains("checked=at-bound-sample outcome=accepted position=$.description", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APrintedSideLossIsRefusedWithTheTakeCodeAndNothingIsWritten()
    {
        // The clipping row part prints the first characters of the cell and drops the rest:
        // the composed text is whole, the page is short, and C-2 sees it.
        var package = AtBoundFixtures.Package();
        package["html/styles.css"] = Encoding.UTF8.GetBytes(
            "body { font-family: \"Liberation Sans\", sans-serif; } "
            + "table.detail td.cell { max-height: 40mm; overflow: hidden; }");
        var renderer = new InMemorySampleRenderer()
            .Answering(_ => new SampleAttempt.Rendered(InMemorySampleRenderer.Facts))
            .Answering(_ => new SampleAttempt.Rendered(InMemorySampleRenderer.Facts))
            .Answering(_ => new SampleAttempt.Failed(new SampleFailure(
                SampleFailureKind.Verification,
                "html/row.html.sbn",
                Reason: "text-dropped")));
        await PublishAsync(package);

        var outcome = await Take(renderer).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Contains("html/row.html.sbn", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("$.description", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("text-dropped", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Consulting", refused.Reason, StringComparison.Ordinal);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AComposedSideLossIsRefusedWhenTheFieldIsAnchoredAndTakenWhenItIsNot()
    {
        var anchored = new InMemorySampleRenderer()
            .Answering(_ => new SampleAttempt.Rendered(InMemorySampleRenderer.Facts))
            .Answering(_ => new SampleAttempt.Rendered(InMemorySampleRenderer.Facts))
            .Answering(_ => new SampleAttempt.Failed(new SampleFailure(
                SampleFailureKind.Verification,
                "html/row.html.sbn",
                AnchorPath: "$.description",
                Reason: "anchor-mismatch")));
        await PublishAsync(AtBoundFixtures.Package());

        var refused = Assert.IsType<RefusedVersion>(
            await Take(anchored).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken));
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Contains("declared as an anchor", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("$.description", refused.Reason, StringComparison.Ordinal);
        await AssertNothingWrittenAsync();

        // The same truncating composition without its anchor: only C-4 could see the loss,
        // and the version declares no such field - the admitted residual, taken.
        var unanchoredPackage = AtBoundFixtures.Package(itemAnchor: null);
        unanchoredPackage["template.yaml"] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(unanchoredPackage["template.yaml"])
                .Replace("version: \"1.0.0\"", "version: \"1.0.1\"", StringComparison.Ordinal));
        var unanchored = new InMemorySampleRenderer().AlwaysRendered();
        await PublishAsync(unanchoredPackage, "1.0.1");

        var taken = AssertTaken(
            await Take(unanchored).TakeAsync(Template, "1.0.1", Consumer, "job-2", TestContext.Current.CancellationToken));
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("checked=at-bound-sample outcome=accepted position=$.description", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMixedContractIsTakenOnThePositionThatAdmitsTheBound()
    {
        const string Mixed = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["sequence", "description", "amount", "note"],
              "properties": {
                "sequence": { "type": "integer", "minimum": 1 },
                "description": { "type": "string", "maxLength": 65536 },
                "amount": { "type": "number" },
                "note": { "type": "string" }
              }
            }
            """;
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        await PublishAsync(AtBoundFixtures.Package(recordSchema: Mixed));

        var record = AssertTaken(await Take(renderer).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken));

        var atBound = JsonNode.Parse(Encoding.UTF8.GetString(renderer.Requests[2].SampleJson.Span))!.AsObject();
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(atBound["items"]!.AsArray()[0]!["note"]!.GetValue<string>()));
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("checked=at-bound-sample outcome=accepted position=$.note", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "detail": { "type": "object", "properties": { "note": { "type": "string" } }, "required": ["note"] } } }""", "$.detail.note", null)]
    [InlineData("""{ "type": "object", "properties": { "tags": { "type": "array", "items": { "type": "string" } } } }""", "$.tags[0]", null)]
    [InlineData("""{ "type": "object", "properties": { "lines": { "type": "array", "items": { "type": "object", "properties": { "text": { "type": "string" } } } } } }""", "$.lines[0].text", null)]
    [InlineData("""{ "type": "object", "properties": { "tags": { "type": "array", "prefixItems": [{ "type": "string" }], "items": { "type": "string", "maxLength": 5 } } } }""", "$.tags[0]", null)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "additionalProperties": { "type": "string" } }""", "$.*", """{ "sequence": 1 }""")]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "patternProperties": { "^x-[a-z]+$": { "type": "string" } } }""", "$.*", null)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "unevaluatedProperties": { "type": "string" } }""", "$.*", """{ "sequence": 1 }""")]
    [InlineData("""{ "type": "object", "properties": { "note": { "type": ["string", "null"] } } }""", "$.note", null)]
    [InlineData("""{ "type": "object", "properties": { "sequence": { "type": "integer" } }, "allOf": [{ "properties": { "description": { "type": "string" } } }] }""", "$.description", null)]
    [InlineData("""{ "type": "object", "properties": { "code": { "type": "string", "pattern": "^[0-9][0-9 ]*$" } } }""", "$.code", null)]
    [InlineData("""{ "type": "object", "properties": { "mode": { "type": "string", "maxLength": 20 }, "amount": { "type": "number" } }, "required": ["mode"], "if": { "properties": { "mode": { "const": "detailed" } }, "required": ["mode"] }, "then": { "properties": { "description": { "type": "string" } } }, "else": { "properties": { "note": { "type": "string" } } } }""", "$.description", null)]
    [InlineData("""{ "type": "object", "properties": { "kind": { "type": "string", "maxLength": 20 }, "sequence": { "type": "integer" } }, "dependentSchemas": { "kind": { "properties": { "note": { "type": "string" } } } } }""", "$.note", null)]
    [InlineData("""{ "type": "object", "properties": { "code": { "type": "string", "format": "duration" } } }""", "$.code", null)]
    public async Task AShapeControlIsTakenWithTheGateRunOnItsPosition(string recordSchema, string position, string? sample)
    {
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        await PublishAsync(AtBoundFixtures.Package(
            recordSchema: recordSchema,
            records: sample is null ? null : [sample],
            itemAnchor: "$.code"));

        var record = AssertTaken(await Take(renderer).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken));

        Assert.Contains(
            _logs.Lines,
            line => line.Contains("checked=at-bound-sample outcome=accepted position=" + position, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "type": "object", "additionalProperties": false, "properties": { "description": { "type": "string", "maxLength": 65536 } } }""")]
    [InlineData("""{ "type": "object", "properties": { "description": { "type": "string" } }, "not": { "properties": { "description": { "type": "string" } }, "required": ["description"] } }""")]
    [InlineData("""{ "type": "object", "properties": { "description": { "type": "string", "format": "uuid" } } }""")]
    public async Task AContractThatAdmitsTheBoundNowhereIsTakenWithNoAtBoundRender(string recordSchema)
    {
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        await PublishAsync(AtBoundFixtures.Package(recordSchema: recordSchema));

        var record = AssertTaken(await Take(renderer).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken));

        // Two requests only: the sample and the empty sample. The at-bound render is not
        // spent on a contract that admits the bound nowhere.
        Assert.Equal(2, renderer.Requests.Count);
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("checked=at-bound-sample outcome=not-applicable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANoRecordSampleIsTakenWithItsContractSynthesisedRecord()
    {
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        await PublishAsync(AtBoundFixtures.Package(records: []));

        var record = AssertTaken(await Take(renderer).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken));

        var atBound = JsonNode.Parse(Encoding.UTF8.GetString(renderer.Requests[2].SampleJson.Span))!.AsObject();
        var items = atBound["items"]!.AsArray();
        var synthesised = Assert.Single(items)!.AsObject();
        Assert.Equal(Bound, Encoding.UTF8.GetByteCount(synthesised["description"]!.GetValue<string>()));
        Assert.Contains(
            _logs.Lines,
            line => line.Contains("checked=at-bound-sample outcome=accepted position=$.description", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("outcome=not-applicable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARenderThatCouldNotRunIsRaisedRetryableAndNeverRefused()
    {
        var renderer = new InMemorySampleRenderer()
            .Answering(_ => new SampleAttempt.Rendered(InMemorySampleRenderer.Facts))
            .Answering(_ => new SampleAttempt.Rendered(InMemorySampleRenderer.Facts))
            .Answering(_ => new SampleAttempt.Unavailable(SampleUnavailableCause.RenderTimeout, "html/row.html.sbn"));
        await PublishAsync(AtBoundFixtures.Package());

        var raised = await Assert.ThrowsAsync<SampleRenderUnavailableException>(
            () => Take(renderer).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken));

        Assert.Equal(SampleUnavailableCause.RenderTimeout, raised.Cause);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task TheRepeatAnswersAlreadyTakenWithoutReadingTheSource()
    {
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        await PublishAsync(AtBoundFixtures.Package());
        await Take(renderer).TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var operations = new CountingStore(_source.Store);
        var again = await Take(new InMemorySampleRenderer().AlwaysRendered(), operations)
            .TakeAsync(Template, Version, Consumer, null, TestContext.Current.CancellationToken);

        var taken = Assert.IsType<AlreadyTakenVersion>(again);
        Assert.Equal(Version, taken.Version.Version);
        Assert.Empty(operations.Opened);
    }

    [Fact]
    public async Task ACheckThatRefusesFirstSpendsNoAtBoundRender()
    {
        var renderer = new InMemorySampleRenderer().AlwaysRendered();
        await PublishAsync(AtBoundFixtures.Package());

        var outcome = await Take(renderer, before: new RefusingCheck())
            .TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.Empty(renderer.Requests);
        await AssertNothingWrittenAsync();
    }

    /// <summary>
    /// The source and manifest read the take spends before its checks, measured over the
    /// control package: the record, the manifest and every file of the publication read
    /// through the port, as the take reads them. The figure is written beside the run and
    /// the deadline arithmetic reads the plan's 30 s bound; a figure above it would be
    /// re-derived, never absorbed.
    /// </summary>
    [Fact]
    public async Task TheSourceAndManifestReadFitsItsBound()
    {
        await PublishAsync(AtBoundFixtures.Package());
        var reader = _source.Reader(_options);

        var started = Stopwatch.GetTimestamp();
        var read = await reader.ReadVersionAsync(Template, Version, TestContext.Current.CancellationToken);
        var found = Assert.IsType<SourceVersionRead.Found>(read);
        foreach (var file in found.Publication.Files)
        {
            await reader.OpenFileAsync(
                Template,
                Version,
                file,
                (stream, token) => stream.CopyToAsync(Stream.Null, token),
                TestContext.Current.CancellationToken);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(
            elapsed <= TimeSpan.FromSeconds(SourceAndManifestPlanSeconds),
            $"the source and manifest read took {elapsed.TotalSeconds:0.00} s, above the {SourceAndManifestPlanSeconds} s bound.");

        if (RunEnvironment.RunDirectory is not null)
        {
            RunEnvironment.WriteEvidence(
                "source-read.txt",
                Encoding.UTF8.GetBytes(
                    $"template={Template}\nversion={Version}\nfiles={found.Publication.Files.Count}\n"
                    + $"sourceAndManifestMs={elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)}\n"
                    + $"sourceAndManifestPlanSeconds={SourceAndManifestPlanSeconds}\n"));
        }
    }

    private TemplateTake Take(
        ISampleRenderer renderer,
        IObjectStore? store = null,
        ITemplateVersionCheck? before = null) =>
        new(
            _source.Reader(_options),
            _versions,
            store ?? _source.Store,
            new ManifestReader(),
            new TemplateVersionConflict(_loggerFactory.CreateLogger(TemplateVersionConflict.LogCategory)),
            Checks(renderer, before),
            new TemplateTakeMetrics(),
            _clock,
            Options.Create(_options),
            _loggerFactory.CreateLogger<TemplateTake>());

    private IReadOnlyList<ITemplateVersionCheck> Checks(ISampleRenderer renderer, ITemplateVersionCheck? before)
    {
        var session = new TemplateCheckSession(TimeProvider.System);
        var checks = new List<ITemplateVersionCheck>();
        if (before is not null)
        {
            checks.Add(before);
        }

        checks.Add(new SampleRenderCheck(renderer));
        checks.Add(new AtBoundSampleCheck(
            session,
            renderer,
            _loggerFactory.CreateLogger<AtBoundSampleCheck>()));
        return checks;
    }

    private Task PublishAsync(IReadOnlyDictionary<string, byte[]> files, string version = Version) =>
        _source.PublishAsync(Template, version, files);

    private async Task AssertNothingWrittenAsync()
    {
        Assert.Empty(_versions.Records);
        var keys = new List<string>();
        await foreach (var info in _source.Store.ListAsync(TemplatePackageKeys.ClassPrefix, TestContext.Current.CancellationToken))
        {
            keys.Add(info.Key.ToString());
        }

        Assert.Empty(keys);
    }

    private static TemplateVersionRecord AssertTaken(TemplateTakeOutcome outcome)
    {
        var taken = Assert.IsType<TakenVersion>(outcome);
        return taken.Version;
    }

    /// <summary>A check that refuses before the sample and the at-bound sample run.</summary>
    private sealed class RefusingCheck : ITemplateVersionCheck
    {
        public string Name => "refuses-first";

        public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(TemplateCheckResult.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "html/row.html.sbn: the page totals are declared."));
    }

    /// <summary>A store that records every object read through it.</summary>
    private sealed class CountingStore(IObjectStore inner) : IObjectStore
    {
        private readonly List<string> _opened = [];

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

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            inner.ListAsync(classPrefix, cancellationToken);
    }
}
