using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
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
/// <para>
/// A host carries its own meter identity, so its metric capture holds exactly the
/// instruments it created, whatever else runs in the process beside it. The captured
/// items are added from the pipeline's own thread and read from the case's, so they are
/// written under a lock and read as snapshots.
/// </para>
/// </summary>
internal sealed class TelemetryTestHost : IAsyncDisposable
{
    private static int _identities;

    private readonly IHost _host;
    private readonly CapturedItems<LogRecord> _logs;
    private readonly CapturedItems<Activity> _activities;
    private readonly CapturedItems<Metric> _metrics;

    private TelemetryTestHost(
        IHost host,
        MeterIdentity identity,
        CapturedItems<LogRecord> logs,
        CapturedItems<Activity> activities,
        CapturedItems<Metric> metrics)
    {
        _host = host;
        Identity = identity;
        _logs = logs;
        _activities = activities;
        _metrics = metrics;
    }

    /// <summary>The identity this host's meter is created from.</summary>
    public MeterIdentity Identity { get; }

    public IReadOnlyList<LogRecord> Logs => _logs.Snapshot();

    public IReadOnlyList<Activity> Activities => _activities.Snapshot();

    public IReadOnlyList<Metric> Metrics => _metrics.Snapshot();

    public IServiceProvider Services => _host.Services;

    public ILoggerFactory Loggers => _host.Services.GetRequiredService<ILoggerFactory>();

    public static async Task<TelemetryTestHost> StartAsync(
        string serviceName = ServiceNames.Api,
        MeterIdentity? identity = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool diagnostics = false)
    {
        // A test composition shares its process, so a host that is not given a
        // particular identity gets a fresh one: two hosts never share a meter.
        identity ??= MeterIdentity.ForHost(
            string.Create(CultureInfo.InvariantCulture, $"ReportService.Tests.{Interlocked.Increment(ref _identities)}"));
        var logs = new CapturedItems<LogRecord>();
        var activities = new CapturedItems<Activity>();
        var metrics = new CapturedItems<Metric>();
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
            identity,
            telemetry => telemetry
                .WithMetrics(metric => metric.AddInMemoryExporter(metrics))
                .WithTracing(tracing => tracing.AddInMemoryExporter(activities)),
            logging => logging.AddInMemoryExporter(logs));

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new TelemetryTestHost(host, identity, logs, activities, metrics);
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

    /// <summary>What the exporters wrote, added under a lock and read as a snapshot.</summary>
    private sealed class CapturedItems<T> : ICollection<T>
    {
        private readonly Lock _gate = new();
        private readonly List<T> _items = [];

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _items.Count;
                }
            }
        }

        public bool IsReadOnly => false;

        public void Add(T item)
        {
            lock (_gate)
            {
                _items.Add(item);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _items.Clear();
            }
        }

        public bool Contains(T item)
        {
            lock (_gate)
            {
                return _items.Contains(item);
            }
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            lock (_gate)
            {
                _items.CopyTo(array, arrayIndex);
            }
        }

        public bool Remove(T item)
        {
            lock (_gate)
            {
                return _items.Remove(item);
            }
        }

        public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public IReadOnlyList<T> Snapshot()
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }
}
