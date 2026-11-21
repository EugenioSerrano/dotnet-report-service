using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.Checks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ManifestReader = ReportService.TemplateSource.Manifest.TemplateManifestReader;

namespace ReportService.TemplateSource.Tests.PdfConstraints;

/// <summary>
/// The product's own test-support package at the take: the package the shared
/// fixtures seed names its font family, so the PDF rules this build registers admit
/// it, and the take keeps it as its write-once object and its record. The case reads
/// the package as the take reads it and then takes it, so a helper that lost the
/// declaration fails here instead of turning another change's suite red.
/// </summary>
public sealed class FontFamilyTakeTests : IAsyncDisposable
{
    private const string Consumer = "billing-system";
    private const string Template = "invoice-pdf";
    private const string Version = "1.0.0";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-font-family",
        Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 11, 0, 0, TimeSpan.Zero));

    private readonly InMemoryTemplateVersionStore _versions = new();

    private readonly LogCapture _logs = new();

    private readonly ILoggerFactory _loggerFactory;

    private readonly TemplateCheckSession _session;

    private readonly TemplateSourceClass _source;

    private readonly TemplateSourceOptions _options = new();

    public FontFamilyTakeTests()
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
    public async Task TheSharedFixturePackagePassesTheFontRuleAndIsTaken()
    {
        var files = TemplatePackageFixtures.Valid(Template, Version, Consumer);
        var directory = Directory.CreateTempSubdirectory("template-font-take-").FullName;
        try
        {
            var candidate = Candidate(files, directory);
            var pageAccounting = await new PageAccountingCheck().CheckAsync(candidate, TestContext.Current.CancellationToken);
            var fontFamily = await new NamedFontFamilyCheck(_session).CheckAsync(candidate, TestContext.Current.CancellationToken);
            var gridCell = await new BottomAlignedGridCellCheck(_session).CheckAsync(candidate, TestContext.Current.CancellationToken);

            Assert.True(pageAccounting.Passed, pageAccounting.Reason);
            Assert.True(fontFamily.Passed, fontFamily.Reason);
            Assert.True(gridCell.Passed, gridCell.Reason);
        }
        finally
        {
            TryDelete(directory);
        }

        await _source.PublishAsync(Template, Version, files);

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var taken = Assert.IsType<TakenVersion>(outcome);
        Assert.Equal(Template, taken.Version.TemplateId);
        Assert.Equal(Version, taken.Version.Version);
        Assert.Equal(Consumer, taken.Version.OwnerConsumerId);
        Assert.Equal(64, taken.Version.SourceDescriptorSha256!.Length);
        Assert.Single(_versions.Records);
        _ = Assert.Single(await KeysAsync(TemplatePackageKeys.ClassPrefix));
    }

    [Fact]
    public async Task AVersionWhoseDetailTextNamesNoFamilyIsRefusedAndNothingIsKept()
    {
        await _source.PublishAsync(Template, Version, PdfConstraintFixtures.FontFamilyUnnamed("scriban"));

        var outcome = await Take().TakeAsync(Template, Version, Consumer, "job-1", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedVersion>(outcome);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Contains("names no font family", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(_versions.Records);
        Assert.Empty(await KeysAsync(TemplatePackageKeys.ClassPrefix));
    }

    private static TemplateCandidate Candidate(Dictionary<string, byte[]> files, string directory)
    {
        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        using var stream = File.OpenRead(Path.Combine(directory, "template.yaml"));
        var read = new TemplateManifestReader().Read(stream);
        Assert.True(read.Succeeded, read.Reason);
        var manifest = read.Manifest ?? throw new InvalidOperationException("the fixture's manifest did not type.");
        return new TemplateCandidate(
            manifest.Id ?? string.Empty,
            manifest.Version ?? string.Empty,
            manifest,
            read.CanonicalJson!,
            directory,
            "sha256:" + new string('a', 64));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
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
