using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;

namespace ReportService.Api.Tests.Storage;

/// <summary>
/// The <c>template-source</c> readiness check: a class that lists is Healthy with the
/// provider, the class and the descriptor contract versions, a listing that fails is
/// Unhealthy with the provider and the error and never a path, and the check recovers
/// without a new one - with one log line per change.
/// </summary>
public sealed class TemplateSourceHealthCheckTests : IDisposable
{
    private static readonly TemplateSourceClassLayout Layout = new("template-source", "published", ".json", 64);

    private static readonly TemplateSourceContract Contract = new("filesystem", Layout, ["1.0"]);

    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private string Root => Path.Combine(_directory.Path, "template-source");

    [Fact]
    public async Task AListableClassIsHealthyWithTheClassAndTheVersions()
    {
        var check = Check(store: out _);

        var result = await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("filesystem", result.Data["provider"]);
        Assert.Equal("template-source", result.Data["class"]);
        Assert.Equal("1.0", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Data["descriptorVersions"])));
    }

    [Fact]
    public async Task AListingThatThrowsIsUnhealthyWithoutAPathAndRecoversWithoutANewCheck()
    {
        _ = Check(store: out var store);
        var logs = new LogCapture();
        using var factory = new LoggerFactory([logs]);
        var check = new TemplateSourceHealthCheck(store, Contract, factory.CreateLogger<TemplateSourceHealthCheck>());

        store.Fail = true;
        var failed = await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, failed.Status);
        Assert.Equal("filesystem", failed.Data["provider"]);
        Assert.Equal("storage.transient", failed.Data["error"]);
        Assert.All(failed.Data.Values, value => Assert.DoesNotContain(Root, value.ToString()!, StringComparison.OrdinalIgnoreCase));

        store.Fail = false;
        var recovered = await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, recovered.Status);

        // One line for the failure and one for the recovery: the check reports a change, not a probe.
        Assert.Single(logs.Lines, line => line.Contains("Template source check failed", StringComparison.Ordinal));
        Assert.Single(logs.Lines, line => line.Contains("Template source check recovered", StringComparison.Ordinal));
    }

    private TemplateSourceHealthCheck Check(out SourceClassStore store)
    {
        Directory.CreateDirectory(Root);
        var filesystem = FilesystemObjectStore.OpenReadOnly(
            new FilesystemStorageOptions { Root = Root },
            "TemplateSource:Store:Filesystem",
            _directory.Path,
            NullLogger<FilesystemObjectStore>.Instance);
        store = new SourceClassStore(filesystem);
        return new TemplateSourceHealthCheck(store, Contract, NullLogger<TemplateSourceHealthCheck>.Instance);
    }

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration(TemplateSourceHealthCheck.Name, _ => null!, HealthStatus.Unhealthy, tags: null),
    };
}
