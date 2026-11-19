using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.Checks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ManifestReader = ReportService.TemplateSource.Manifest.TemplateManifestReader;

namespace ReportService.TemplateSource.Tests.PdfConstraints;

/// <summary>
/// The page-subtotal refusal at the take, over the real source read: a version that
/// declares the printed totals is refused when it is taken, with the capability code
/// and no record, no object and no leftover; a version that declares them off or
/// leaves the member out is taken and kept as its write-once object and its record,
/// and the second take of it answers that it is already taken. The take's own log
/// line is asserted for what it carries.
/// </summary>
public sealed class PageAccountingTakeTests : IAsyncDisposable
{
    private const string Consumer = "billing-system";
    private const string Template = PdfConstraintFixtures.TemplateId;
    private const string Version = PdfConstraintFixtures.Version;
    private const string Engine = "scriban";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-pdf-constraints",
        Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 9, 30, 0, TimeSpan.Zero));

    private readonly InMemoryTemplateVersionStore _versions = new();

    private readonly LogCapture _logs = new();

    private readonly ILoggerFactory _loggerFactory;

    private readonly TemplateCheckSession _session;

    private readonly TemplateSourceClass _source;

    private readonly TemplateSourceOptions _options = new();

    public PageAccountingTakeTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        _session = new TemplateCheckSession(_clock);
        Directory.CreateDirectory(_root);
        _source = TemplateSourceClass.CreateAsync(Path.Combine(_root, "source")).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _session.Dispose();
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
    public async Task AVersionThatDeclaresPageTotalsIsRefusedAndNothingIsKept()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.PageTotalsEnabled(Engine));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.True(
            refused.Code == TemplateCheckCodes.UnsupportedCapability,
            "the refusal must be the capability one, not an earlier rule's: " + refused.Code + " - " + refused.Reason);
        Assert.False(refused.Retryable);
        Assert.Contains("page subtotals", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_versions.Records);
        Assert.Empty(await KeysAsync(TemplatePackageKeys.ClassPrefix));
    }

    [Fact]
    public async Task TheRefusalIsLoggedWithTheTakeTheTemplateAndTheCode()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.PageTotalsEnabled(Engine));

        _ = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        Assert.Contains(
            _logs.Lines,
            line => line.Contains("refused", StringComparison.OrdinalIgnoreCase)
                && line.Contains(Template, StringComparison.Ordinal)
                && line.Contains(Version, StringComparison.Ordinal)
                && line.Contains(Consumer, StringComparison.Ordinal)
                && line.Contains(TemplateCheckCodes.UnsupportedCapability, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheControlPackageIsTakenAndKeptAsItsObjectAndRecord()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.Control(Engine));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var taken = Assert.IsType<TakenVersion>(outcome);
        Assert.Equal(Template, taken.Version.TemplateId);
        Assert.Equal(Version, taken.Version.Version);
        Assert.Equal(Consumer, taken.Version.OwnerConsumerId);
        Assert.Equal(64, taken.Version.SourceDescriptorSha256!.Length);
        Assert.Single(_versions.Records);
        var copies = await KeysAsync(TemplatePackageKeys.ClassPrefix);
        _ = Assert.Single(copies);
    }

    [Fact]
    public async Task ASecondTakeOfTheTakenVersionIsAlreadyTaken()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.Control(Engine));
        _ = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var again = await Take().TakeAsync(Template, Version, Consumer, "job-2", TestContext.Current.CancellationToken);

        _ = Assert.IsType<AlreadyTakenVersion>(again);
        _ = Assert.Single(await KeysAsync(TemplatePackageKeys.ClassPrefix));
        Assert.Single(_versions.Records);
    }

    [Fact]
    public async Task AVersionWithoutThePageTotalsMemberIsTaken()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.PageTotalsAbsent(Engine));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        _ = Assert.IsType<TakenVersion>(outcome);
    }

    [Fact]
    public async Task TheSharedFixturePackagePassesTheDeliveredChecksAndIsTaken()
    {
        await _source.PublishAsync(Template, Version, TemplatePackageFixtures.Valid(Template, Version, Consumer));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var taken = Assert.IsType<TakenVersion>(outcome);
        Assert.Equal(64, taken.Version.SourceDescriptorSha256!.Length);
        Assert.Single(_versions.Records);
        _ = Assert.Single(await KeysAsync(TemplatePackageKeys.ClassPrefix));
    }

    [Fact]
    public void TheRefusalCarriesTheWordTheRenderSideGuardUses()
    {
        Assert.Equal("pdf-page-accounting", PageAccountingCheck.RuleId);
        Assert.Equal("page-accounting", PageAccountingCheck.ReasonToken);
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

    private TemplateTake Take() =>
        new(
            _source.Reader(_options),
            _versions,
            _source.Store,
            new ManifestReader(),
            new TemplateVersionConflict(_loggerFactory.CreateLogger(TemplateVersionConflict.LogCategory)),
            Checks(),
            new TemplateTakeMetrics(),
            _clock,
            Options.Create(_options),
            _loggerFactory.CreateLogger<TemplateTake>());

    /// <summary>The delivered checks in their fixed order, with the three PDF rules appended as the build registers them.</summary>
    private IReadOnlyList<ITemplateVersionCheck> Checks() =>
    [
        new ManifestAndPathsCheck(_session),
        new SchemaCheck(_session),
        new SampleCheck(_session, _clock),
        new AnchorCheck(_session),
        new SyntaxCheck(_session),
        new IncludeAndAssetCheck(_session),
        new TemplateLintCheck(_session),
        new EngineVersionCheck(),
        new PageAccountingCheck(),
        new NamedFontFamilyCheck(_session),
        new BottomAlignedGridCellCheck(_session),
    ];
}
