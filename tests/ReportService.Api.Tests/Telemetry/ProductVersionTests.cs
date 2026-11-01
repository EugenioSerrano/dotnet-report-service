using Microsoft.Extensions.DependencyInjection;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// The version a process reports: the informational version carries the commit
/// the build came from, and reading or parsing it never throws — an absent or
/// malformed value reports "unknown" instead.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class ProductVersionTests
{
    [Fact]
    public void TheCommitIsEverythingAfterTheMetadataSeparator()
    {
        Assert.Equal("abc123", ProductVersion.CommitOf("0.2.0-dev+abc123"));
        Assert.Equal("d199ca299077f1bc4112e00556a2c819f14ee184", ProductVersion.CommitOf("0.2.0-dev+d199ca299077f1bc4112e00556a2c819f14ee184"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.2.0-dev")]
    [InlineData("0.2.0-dev+")]
    [InlineData("not a version")]
    public void AValueWithoutACommitIsUnknownAndNeverThrows(string? informationalVersion) =>
        Assert.Equal(ProductVersion.UnknownCommit, ProductVersion.CommitOf(informationalVersion));

    [Fact]
    public void TheRunningBuildReportsAVersionAndItsCommit()
    {
        var version = ProductVersion.Informational;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain(ProductVersion.UnknownCommit, version, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{40}$", ProductVersion.CommitOf(version));
    }

    [Fact]
    public async Task TheStartUpLineNamesTheVersionTheRuntimeAndTheCommit()
    {
        await using var host = await TelemetryTestHost.StartAsync();

        var line = Assert.Single(host.Logs, record => record.CategoryName == "ReportService.Startup");
        var message = line.FormattedMessage ?? line.Body ?? string.Empty;

        Assert.Contains("service=report-service-api", message, StringComparison.Ordinal);
        Assert.Contains("version=" + ProductVersion.Informational, message, StringComparison.Ordinal);
        Assert.Contains("runtime=" + Environment.Version, message, StringComparison.Ordinal);
        Assert.Contains("commit=" + ProductVersion.CommitOf(ProductVersion.Informational), message, StringComparison.Ordinal);
        Assert.Contains("started", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServiceVersionTheProcessDeclaresIsTheBuildsInformationalVersion()
    {
        await using var host = await TelemetryTestHost.StartAsync(diagnostics: true);

        host.CollectMetrics();
        var read = host.Capture.Take(1000);

        Assert.Equal("report-service-api", read.Resource["service.name"]);
        Assert.Equal(ProductVersion.Informational, read.Resource["service.version"]);
        Assert.False(string.IsNullOrWhiteSpace(read.Resource["service.instance.id"]));
        Assert.Equal("Development", read.Resource["deployment.environment"]);
    }

    [Fact]
    public async Task TheBuildInfoGaugeCarriesTheVersionAndTheRuntimeAndNothingElse()
    {
        await using var host = await TelemetryTestHost.StartAsync(diagnostics: true);
        host.Services.GetRequiredService<BuildInfoMetric>().Record();

        host.CollectMetrics();
        var point = Assert.Single(host.Capture.Take(1000).Points, candidate => candidate.Instrument == BuildInfoMetric.InstrumentName);

        Assert.Equal("Gauge", point.Type);
        Assert.Equal(1d, point.Value);
        Assert.Equal(ProductVersion.Informational, point.Tags.Single(tag => tag.Key == "version").Value);
        Assert.False(string.IsNullOrWhiteSpace(point.Tags.Single(tag => tag.Key == "runtime").Value));
        Assert.Equal(2, point.Tags.Count);
    }
}
