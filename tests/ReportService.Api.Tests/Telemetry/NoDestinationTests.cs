using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// Where telemetry goes is configuration. With no destination configured a
/// process registers no destination exporter and starts without an export
/// attempt; the endpoint and the protocol are read from the configuration and
/// the standard OpenTelemetry variables, and nothing else selects them.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class NoDestinationTests
{
    [Fact]
    public void NoConfiguredEndpointMeansNoDestination()
    {
        Assert.Null(TelemetryDefaults.Endpoint(Configuration()));
        Assert.Null(TelemetryDefaults.Endpoint(Configuration(
            (TelemetryDefaults.OtlpEndpointEnvironmentVariable, "   "))));
    }

    [Fact]
    public void TheEndpointComesFromTheSettingOrTheStandardVariable()
    {
        Assert.Equal(
            new Uri("http://localhost:4317"),
            TelemetryDefaults.Endpoint(Configuration((TelemetryDefaults.OtlpEndpointSetting, "http://localhost:4317"))));
        Assert.Equal(
            new Uri("http://collector:4317"),
            TelemetryDefaults.Endpoint(Configuration((TelemetryDefaults.OtlpEndpointEnvironmentVariable, "http://collector:4317"))));
        Assert.Equal(
            new Uri("http://localhost:4318"),
            TelemetryDefaults.Endpoint(Configuration(
                (TelemetryDefaults.OtlpEndpointSetting, "http://localhost:4318"),
                (TelemetryDefaults.OtlpEndpointEnvironmentVariable, "http://collector:4317"))));
        Assert.Null(TelemetryDefaults.Endpoint(Configuration((TelemetryDefaults.OtlpEndpointSetting, "not an address"))));
    }

    [Fact]
    public void TheProtocolFollowsTheDeploymentAndDefaultsToGrpc()
    {
        Assert.Equal(OpenTelemetry.Exporter.OtlpExportProtocol.Grpc, TelemetryDefaults.Protocol(Configuration()));
        Assert.Equal(
            OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf,
            TelemetryDefaults.Protocol(Configuration((TelemetryDefaults.OtlpProtocolEnvironmentVariable, "http/protobuf"))));
    }

    [Fact]
    public async Task WithNoDestinationTheHostStartsAndRegistersNoDestinationExporter()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TelemetryDiagnosticsSettings.EnabledSetting] = "false",
        });
        builder.AddServiceDefaults(ServiceNames.Api, MeterIdentity.ForHost(TelemetryDefaults.MeterName));

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(host.Services.GetServices<BaseExporter<Metric>>());
        Assert.Empty(host.Services.GetServices<BaseExporter<Activity>>());
        Assert.Empty(host.Services.GetServices<BaseExporter<OpenTelemetry.Logs.LogRecord>>());
        Assert.NotNull(host.Services.GetService<MeterProvider>());

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
