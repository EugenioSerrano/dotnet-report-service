using Microsoft.Extensions.Logging;
using ReportService.Testing;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateSource.Tests;

/// <summary>
/// The warning a changed taken version produces: one, carrying both hashes,
/// and no refusal of anything.
/// </summary>
public sealed class TemplateVersionConflictTests
{
    [Fact]
    public void TheWarningCarriesBothHashesAndNeverRefuses()
    {
        var capture = new LogCapture();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        var conflict = new TemplateVersionConflict(factory.CreateLogger(TemplateVersionConflict.LogCategory));

        conflict.Report(Record(), new string('b', 64), new string('c', 40), "report-job-1");

        var line = Assert.Single(capture.Lines);
        Assert.StartsWith("Warning: ReportService.TemplateSource:", line, StringComparison.Ordinal);
        Assert.Contains(TemplateVersionConflict.Code, line, StringComparison.Ordinal);
        Assert.Contains("invoice-energy", line, StringComparison.Ordinal);
        Assert.Contains("1.1.0", line, StringComparison.Ordinal);
        Assert.Contains(new string('a', 64), line, StringComparison.Ordinal);
        Assert.Contains(new string('b', 64), line, StringComparison.Ordinal);
        Assert.Contains(new string('c', 40), line, StringComparison.Ordinal);
        Assert.Contains("Publish the change under a new version.", line, StringComparison.Ordinal);
        Assert.Contains("report-job-1", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportWithoutAJobSaysSoRatherThanLeavingTheFieldOut()
    {
        var capture = new LogCapture();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(capture));
        var conflict = new TemplateVersionConflict(factory.CreateLogger(TemplateVersionConflict.LogCategory));

        conflict.Report(Record(), new string('b', 64), new string('c', 40));

        Assert.Contains("(none)", Assert.Single(capture.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarningNeedsARecordAndBothHashes()
    {
        var conflict = new TemplateVersionConflict(LoggerFactory.Create(builder => { }).CreateLogger("test"));

        Assert.Throws<ArgumentNullException>(() => conflict.Report(null!, new string('b', 64), new string('c', 40)));
        Assert.Throws<ArgumentException>(() => conflict.Report(Record(), string.Empty, new string('c', 40)));
        Assert.Throws<ArgumentException>(() => conflict.Report(Record(), new string('b', 64), string.Empty));
        Assert.Throws<ArgumentNullException>(() => new TemplateVersionConflict(null!));
    }

    [Fact]
    public async Task TheChecksRunInOrderAndTheFirstRefusalEndsTheTake()
    {
        var ran = new List<string>();
        ITemplateVersionCheck[] checks =
        [
            new Says("first", TemplateCheckResult.Pass(), ran),
            new Says("second", TemplateCheckResult.Refuse(TemplateCheckCodes.TemplateInvalid, "The engine is missing in template.yaml."), ran),
            new Says("third", TemplateCheckResult.Pass(), ran),
        ];

        var result = await TemplateVersionChecks.RunAsync(checks, Candidate(), TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Equal("The engine is missing in template.yaml.", result.Reason);
        Assert.Equal(["first", "second"], ran);
    }

    [Fact]
    public async Task NoCheckRegisteredMeansTheVersionPasses()
    {
        var result = await TemplateVersionChecks.RunAsync([], Candidate(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Null(result.Code);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task TheChecksNeedTheirInput()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => TemplateVersionChecks.RunAsync(null!, Candidate(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => TemplateVersionChecks.RunAsync([], null!, TestContext.Current.CancellationToken));
    }

    private static TemplateCandidate Candidate() => new(
        "invoice-energy",
        "1.1.0",
        TemplateManifestJson.ReadTyped("""{"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0"}"""),
        Path.GetTempPath(),
        new string('1', 40));

    private static TemplateVersionRecord Record() => new(
        "invoice-energy",
        "1.1.0",
        "billing-system",
        Shared: false,
        "1.0",
        TemplateEngine.Scriban,
        TemplateEngineCatalog.ScribanVersion,
        "schema.json",
        new string('1', 40),
        new string('2', 40),
        new string('a', 64),
        128,
        FileCount: 2,
        TemplatePackageKeys.ForVersion("billing-system", "invoice-energy", "1.1.0", new string('a', 64)),
        TemplateManifestJson.ReadTyped("""{"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0"}"""),
        """{"manifestVersion":"1.0","id":"invoice-energy","version":"1.1.0"}""",
        new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    private sealed class Says(string name, TemplateCheckResult result, List<string> ran) : ITemplateVersionCheck
    {
        public string Name => name;

        public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
        {
            Assert.NotNull(candidate);
            ran.Add(name);
            return Task.FromResult(result);
        }
    }
}
