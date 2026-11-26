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
/// The page-accounting admission at the take, over the real source read: a version that
/// declares the printed totals without the accounting contract is refused as
/// <c>TEMPLATE_INVALID</c> by the rules that replaced the old capability refusal, with no
/// record, no object and no leftover; a version that declares the contract — a grid row
/// with an amount role, a group total role, the reserved band and its three labels — is
/// taken and kept as its write-once object and its record; a version that declares no
/// totals is unaffected. The take's own log line is asserted for what it carries.
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
    public async Task AVersionThatDeclaresPageTotalsWithoutTheAccountingContractIsRefusedAndNothingIsKept()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.PageTotalsEnabled(Engine));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Contains("table markup", refused.Reason, StringComparison.OrdinalIgnoreCase);
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
                && line.Contains(TemplateCheckCodes.TemplateInvalid, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("scriban")]
    [InlineData("handlebars")]
    [InlineData("liquid")]
    public async Task AVersionWithTheAccountingContractIsTaken(string engine)
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.Accounted(engine));

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
    public async Task AVersionWithPageTotalsButNoReservedBandIsRefusedWithTheFooterRule()
    {
        var manifest = PdfConstraintFixtures.AccountedManifest(Engine);
        var start = manifest.IndexOf("      footer:", StringComparison.Ordinal);
        var end = manifest.IndexOf("    verification:", StringComparison.Ordinal);
        var package = PdfConstraintFixtures.Accounted(Engine);
        package["template.yaml"] = System.Text.Encoding.UTF8.GetBytes(manifest[..start] + manifest[end..]);
        await _source.PublishAsync(Template, Version, package);

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, refused.Code);
        Assert.Contains("footer", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_versions.Records);
    }

    [Fact]
    public async Task AVersionWhoseBandCrossesTheBottomMarginIsRefused()
    {
        var package = PdfConstraintFixtures.Accounted(Engine);
        package["template.yaml"] = System.Text.Encoding.UTF8.GetBytes(
            PdfConstraintFixtures.AccountedManifest(Engine).Replace("topMm: 282", "topMm: 270", StringComparison.Ordinal));
        await _source.PublishAsync(Template, Version, package);

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, refused.Code);
        Assert.Contains("bottom margin", refused.Reason, StringComparison.OrdinalIgnoreCase);
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
    public void TheTwoRulesCarryTheirOwnIdentifiers()
    {
        Assert.Equal("pdf-accounted-detail", AccountedDetailMarkupCheck.RuleId);
        Assert.Equal("pdf-accounting-footer", AccountingFooterCheck.RuleId);
        Assert.Equal("AccountedDetailMarkupCheck", new AccountedDetailMarkupCheck(_session).Name);
        Assert.Equal("AccountingFooterCheck", new AccountingFooterCheck().Name);
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

    /// <summary>The delivered checks in their fixed order, with the PDF rules appended as the build registers them.</summary>
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
        new AccountedDetailMarkupCheck(_session),
        new AccountingFooterCheck(),
        new NamedFontFamilyCheck(_session),
        new BottomAlignedGridCellCheck(_session),
    ];
}
