using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// A process built from the shared telemetry registration, with one in-memory
/// exporter per signal so a test can read what the pipeline produced. The
/// exporters are added through the registration's seam and sit in the same
/// pipeline as the process's own processors, so redaction and job enrichment
/// apply to what the test reads.
/// </summary>
internal sealed class TelemetryTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private TelemetryTestHost(IHost host, List<LogRecord> logs, List<Activity> activities, List<Metric> metrics)
    {
        _host = host;
        Logs = logs;
        Activities = activities;
        Metrics = metrics;
    }

    public List<LogRecord> Logs { get; }

    public List<Activity> Activities { get; }

    public List<Metric> Metrics { get; }

    public IServiceProvider Services => _host.Services;

    public ILoggerFactory Loggers => _host.Services.GetRequiredService<ILoggerFactory>();

    public static async Task<TelemetryTestHost> StartAsync(
        string serviceName = ServiceNames.Api,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool diagnostics = false)
    {
        var logs = new List<LogRecord>();
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TelemetryDiagnosticsSettings.EnabledSetting] = diagnostics ? "true" : "false",
            [TelemetryDiagnosticsSettings.ExportIntervalSetting] = "1000",
        };
        foreach (var setting in settings ?? new Dictionary<string, string?>(StringComparer.Ordinal))
        {
            configuration[setting.Key] = setting.Value;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddServiceDefaults(
            serviceName,
            telemetry => telemetry
                .WithMetrics(metric => metric.AddInMemoryExporter(metrics))
                .WithTracing(tracing => tracing.AddInMemoryExporter(activities)),
            logging => logging.AddInMemoryExporter(logs));

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new TelemetryTestHost(host, logs, activities, metrics);
    }

    /// <summary>Collects the metric readers now, so the capture and the in-memory exporter have both exported.</summary>
    public void CollectMetrics() => _host.Services.GetRequiredService<MeterProvider>().ForceFlush();

    /// <summary>Reads the capture of a host that has the development diagnostics on.</summary>
    public TelemetryCaptureBuffer Capture => _host.Services.GetRequiredService<TelemetryCaptureBuffer>();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TestContext.Current.CancellationToken);
        _host.Dispose();
    }
}
