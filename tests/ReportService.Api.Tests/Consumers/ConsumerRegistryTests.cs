using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ReportService.Api.Consumers;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Api.Tests.Consumers;

/// <summary>
/// The registry on a running service: a valid change replaces the snapshot, an
/// invalid change is refused and the last valid registry stays in force.
/// </summary>
public sealed class ConsumerRegistryTests : IDisposable
{
    private const string PlantedSecret = "planted-secret-5d2b88";

    private static readonly Dictionary<string, string?> Initial = new()
    {
        ["consumers:0:consumerId"] = "dev-consumer",
        ["consumers:0:active"] = "true",
        ["consumers:1:consumerId"] = "dev-consumer-disabled",
        ["consumers:1:active"] = "false",
    };

    private readonly LogCapture _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public ConsumerRegistryTests() => _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs));

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public void InitialValidRegistryIsAvailable()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);

        Assert.Equal(["dev-consumer", "dev-consumer-disabled"], registry.Current.Entries.Select(e => e.ConsumerId));
        Assert.Single(Lines("Consumer registry loaded: 2 entries"));
    }

    [Fact]
    public void InvalidInitialRegistryStopsStartNamingTheEntryAndNoValue()
    {
        var configuration = new ReplaceableConfiguration(new Dictionary<string, string?>
        {
            ["consumers:0:consumerId"] = "dev-consumer",
            ["consumers:0:token"] = PlantedSecret,
        });

        var error = Assert.Throws<ConfigurationError>(() => Registry(configuration));

        Assert.Equal("consumers[0]", error.Setting);
        Assert.Equal("setting 'consumers[0]': consumer 'dev-consumer': active is required", error.Message);
        Assert.DoesNotContain(PlantedSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidChangeReplacesTheSnapshotWithANewFingerprint()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);
        var before = registry.Current;

        configuration.Replace(With(("consumers:0:active", "false")));

        Assert.NotSame(before, registry.Current);
        Assert.NotEqual(before.Fingerprint, registry.Current.Fingerprint);
        Assert.False(registry.Current.Entries[0].Active);
        Assert.Single(Lines($"Consumer registry changed: 2 entries, fingerprint {registry.Current.Fingerprint}"));
    }

    [Fact]
    public void AddedAndRemovedConsumersAreApplied()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);

        configuration.Replace(With(("consumers:2:consumerId", "third"), ("consumers:2:active", "true")));
        Assert.Equal(3, registry.Current.Entries.Count);

        configuration.Replace(new Dictionary<string, string?> { ["consumers:0:consumerId"] = "third", ["consumers:0:active"] = "true" });
        Assert.Equal(["third"], registry.Current.Entries.Select(e => e.ConsumerId));
    }

    [Fact]
    public void InvalidChangeIsRefusedKeepingTheLastValidRegistryAndLoggingTheEntryWithoutValues()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);
        var before = registry.Current;

        configuration.Replace(With(
            ("consumers:1:consumerId", "dev-consumer"),
            ("consumers:1:secretNote", PlantedSecret),
            ("consumers:2:consumerId", "third"),
            ("consumers:2:password", PlantedSecret)));

        Assert.Same(before, registry.Current);
        var refusal = Assert.Single(Lines("Consumer registry change refused"));
        Assert.Contains("consumers[1]: consumer 'dev-consumer' duplicates consumers[0]", refusal, StringComparison.Ordinal);
        Assert.Contains("consumers[2]: consumer 'third': active is required", refusal, StringComparison.Ordinal);
        Assert.StartsWith("Warning:", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains(PlantedSecret, StringComparison.Ordinal));
    }

    [Fact]
    public void UnreadableFileKeepsTheLastValidRegistry()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);
        var before = registry.Current;

        // A file that cannot be parsed leaves the configuration empty until it is fixed.
        configuration.Replace(new Dictionary<string, string?>());

        Assert.Same(before, registry.Current);
        Assert.Single(Lines("the registry has no consumers list"));

        configuration.Replace(Initial);
        Assert.Equal(before.Fingerprint, registry.Current.Fingerprint);
    }

    [Fact]
    public void EmptyRegistryIsAValidChange()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);

        configuration.Replace(new Dictionary<string, string?> { ["consumers"] = null });

        Assert.Empty(registry.Current.Entries);
        Assert.Single(Lines("Consumer registry changed: 0 entries"));
    }

    [Fact]
    public void UnchangedReloadKeepsTheSnapshotAndLogsNothing()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);
        var before = registry.Current;

        configuration.Replace(Initial);

        Assert.Same(before, registry.Current);
        Assert.Empty(Lines("Consumer registry changed"));
    }

    [Fact]
    public void UnknownPropertyAfterAChangeIsLoggedAsAWarning()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);

        configuration.Replace(With(("consumers:0:apiKey", PlantedSecret), ("consumers:0:active", "false")));

        Assert.Single(Lines("unknown property 'apiKey' is ignored"));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains(PlantedSecret, StringComparison.Ordinal));
    }

    [Fact]
    public void DisposedRegistryNoLongerFollowsChanges()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        var registry = Registry(configuration);
        var before = registry.Current;
        registry.Dispose();

        configuration.Replace(With(("consumers:0:active", "false")));

        Assert.Same(before, registry.Current);
    }

    [Fact]
    public async Task HealthCheckIsAlwaysHealthyWithCountAndFingerprintOfTheLastValidRegistry()
    {
        var configuration = new ReplaceableConfiguration(Initial);
        using var registry = Registry(configuration);
        var check = new ConsumerRegistryHealthCheck(registry);
        configuration.Replace(new Dictionary<string, string?> { ["consumers:0:consumerId"] = "" });

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(["entries", "fingerprint"], result.Data.Keys);
        Assert.Equal(2, result.Data["entries"]);
        Assert.Equal(registry.Current.Fingerprint, result.Data["fingerprint"]);
    }

    private ConsumerRegistry Registry(ReplaceableConfiguration configuration) =>
        new(configuration.Root, _loggerFactory.CreateLogger<ConsumerRegistry>());

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] changes)
    {
        var values = new Dictionary<string, string?>(Initial);
        foreach (var (key, value) in changes)
        {
            values[key] = value;
        }

        return values;
    }

    private List<string> Lines(string fragment) =>
        [.. _logs.Lines.Where(l => l.Contains(fragment, StringComparison.Ordinal))];
}
