using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Configuration;

/// <summary>Validated settings: a missing or invalid setting stops the host before it is ready, naming the setting and never its value.</summary>
public sealed class ServiceConfigurationTests
{
    private const string PlantedSecret = "planted-secret-7f3a9c";

    [Fact]
    public async Task MissingRequiredSettingStopsStartNamingTheSettingWithoutAnyValue()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Sample:Password"] = PlantedSecret,
        });

        var error = await Assert.ThrowsAsync<ConfigurationError>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Sample:Endpoint", error.Setting);
        Assert.Equal("setting 'Sample:Endpoint': is required", error.Message);
        Assert.DoesNotContain(PlantedSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySettingIsReportedLikeAMissingOne()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Sample:Endpoint"] = "   ",
            ["Sample:Password"] = PlantedSecret,
        });

        var error = await Assert.ThrowsAsync<ConfigurationError>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Sample:Endpoint", error.Setting);
        Assert.DoesNotContain(PlantedSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSecretSettingIsNamedButItsValueNeverAppears()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Sample:Endpoint"] = "host:1",
            ["Sample:Password"] = "short",
        });

        var error = await Assert.ThrowsAsync<ConfigurationError>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("setting 'Sample:Password': must be at least 8 characters", error.Message);
        Assert.DoesNotContain("short'", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.Data);
    }

    [Fact]
    public async Task EveryProblemIsReportedTogether()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Sample:Password"] = "short",
        });

        var error = await Assert.ThrowsAsync<ConfigurationError>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, error.Problems.Count);
        Assert.Equal(
            "setting 'Sample:Endpoint': is required; setting 'Sample:Password': must be at least 8 characters",
            error.Message);
    }

    [Fact]
    public async Task ValidConfigurationBuildsAndStartsTheHostWithBoundSettings()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Sample:Endpoint"] = "host:1",
            ["Sample:Password"] = PlantedSecret,
        });

        await host.StartAsync(TestContext.Current.CancellationToken);
        var settings = host.Services.GetRequiredService<IOptions<SampleSettings>>().Value;
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("host:1", settings.Endpoint);
        Assert.Null(settings.Optional);
    }

    [Fact]
    public void ValidationCanBeRunWithoutAHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sample:Password"] = PlantedSecret })
            .Build();

        var settings = ServiceConfiguration.Bind<SampleSettings>(configuration, "Sample");
        var error = Assert.Throws<ConfigurationError>(() => ServiceConfiguration.Validate(settings, "Sample"));

        Assert.Equal("Sample:Endpoint", error.Setting);
        Assert.Equal("is required", error.Reason);
    }

    [Fact]
    public void RegisteringTheSameConfigurationTwiceValidatesItOnce()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceConfiguration();
        builder.AddServiceConfiguration();

        var validators = builder.Services.Count(d => d.ImplementationType == typeof(ConfigurationValidation));

        Assert.Equal(1, validators);
    }

    [Fact]
    public void ErrorWithoutProblemsIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ConfigurationError([]));
    }

    private static IHost BuildHost(Dictionary<string, string?> values)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(values);
        builder.AddServiceConfiguration();
        builder.AddValidatedConfiguration<SampleSettings>("Sample");
        return builder.Build();
    }

    /// <summary>One required setting, one secret-valued setting and one optional setting.</summary>
    public sealed class SampleSettings : IValidatedSettings
    {
        public string? Endpoint { get; set; }

        public string? Password { get; set; }

        public string? Optional { get; set; }

        public IEnumerable<ConfigurationProblem> Validate()
        {
            if (string.IsNullOrWhiteSpace(Endpoint))
            {
                yield return new ConfigurationProblem(nameof(Endpoint), "is required");
            }

            if (Password is { Length: < 8 })
            {
                yield return new ConfigurationProblem(nameof(Password), "must be at least 8 characters");
            }
        }
    }
}
