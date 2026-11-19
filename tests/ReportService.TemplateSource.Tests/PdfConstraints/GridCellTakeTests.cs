using Microsoft.Extensions.Logging;
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
/// The grid-cell refusal at the take, over the real source read: a version whose row
/// part is a grid with a bottom-aligned cell that may split is refused with the
/// template-invalid code, and nothing is kept — no record, no object, no leftover. A
/// version that breaks several constraints is refused once, for the first one the
/// fixed order names.
/// </summary>
public sealed class GridCellTakeTests : IAsyncDisposable
{
    private const string Consumer = "billing-system";
    private const string Template = PdfConstraintFixtures.TemplateId;
    private const string Version = PdfConstraintFixtures.Version;
    private const string Engine = "scriban";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-grid-cell",
        Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 11, 20, 0, TimeSpan.Zero));

    private readonly InMemoryTemplateVersionStore _versions = new();

    private readonly LogCapture _logs = new();

    private readonly ILoggerFactory _loggerFactory;

    private readonly TemplateCheckSession _session;

    private readonly TemplateSourceClass _source;

    private readonly TemplateSourceOptions _options = new();

    public GridCellTakeTests()
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
    public async Task AGridCellAlignedToTheBottomIsRefusedAndNothingIsKept()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.GridBottom(Engine));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Contains("may split across pages", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("aligned to the bottom", refused.Reason, StringComparison.Ordinal);
        Assert.Contains("the cell class \"amount\"", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(_versions.Records);
        Assert.Empty(await KeysAsync(TemplatePackageKeys.ClassPrefix));
    }

    [Fact]
    public async Task TheMultiFaultVersionIsRefusedOnceForItsFirstFault()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.MultiFault(Engine));

        var first = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);
        var second = await Take().TakeAsync(Template, Version, Consumer, "job-2", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(first);
        Assert.Equal(TemplateCheckCodes.UnsupportedCapability, refused.Code);
        Assert.Contains("page subtotals", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("template.yaml:", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(_versions.Records);
        Assert.Empty(await KeysAsync(TemplatePackageKeys.ClassPrefix));

        var again = Assert.IsType<RefusedVersion>(second);
        Assert.Equal(refused.Code, again.Code);
        Assert.Equal(refused.Reason, again.Reason);
        Assert.Empty(_versions.Records);
        Assert.Empty(await KeysAsync(TemplatePackageKeys.ClassPrefix));
    }

    [Theory]
    [InlineData("scriban")]
    [InlineData("handlebars")]
    [InlineData("liquid")]
    public async Task TheControlPackageIsTakenPerEngineAndTheRepeatIsAlreadyTaken(string engine)
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.Control(engine));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var taken = Assert.IsType<TakenVersion>(outcome);
        Assert.Equal(Template, taken.Version.TemplateId);
        Assert.Equal(Version, taken.Version.Version);
        Assert.Equal(Consumer, taken.Version.OwnerConsumerId);
        Assert.Equal(64, taken.Version.SourceDescriptorSha256!.Length);
        Assert.Single(_versions.Records);
        _ = Assert.Single(await KeysAsync(TemplatePackageKeys.ClassPrefix));

        var again = await Take().TakeAsync(Template, Version, Consumer, "job-2", TestContext.Current.CancellationToken);

        _ = Assert.IsType<AlreadyTakenVersion>(again);
        _ = Assert.Single(await KeysAsync(TemplatePackageKeys.ClassPrefix));
        Assert.Single(_versions.Records);
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
