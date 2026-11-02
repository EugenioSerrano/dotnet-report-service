using System.Collections;
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
/// <para>
/// The trace pipeline is process-wide, so the activity capture keeps the spans of
/// the unit of work the test opened with <see cref="BeginUnit"/> and nothing else:
/// the unit's trace is what tells one test's spans from another's. Every read
/// returns a snapshot, and a case that opens no unit reads an empty capture.
/// </para>
/// </summary>
internal sealed class TelemetryTestHost : IAsyncDisposable
{
    /// <summary>The source a unit of work's root activity is started from; the product's pipeline does not subscribe to it.</summary>
    private const string UnitSourceName = "ReportService.Tests.CaptureUnit";

    private readonly IHost _host;
    private readonly SignalSnapshot<LogRecord> _logs;
    private readonly UnitActivityCapture _activities;
    private readonly SignalSnapshot<Metric> _metrics;
    private readonly ActivitySource _units;
    private readonly ActivityListener _unitListener;

    private TelemetryTestHost(
        IHost host,
        SignalSnapshot<LogRecord> logs,
        UnitActivityCapture activities,
        SignalSnapshot<Metric> metrics,
        ActivitySource units,
        ActivityListener unitListener)
    {
        _host = host;
        _logs = logs;
        _activities = activities;
        _metrics = metrics;
        _units = units;
        _unitListener = unitListener;
    }

    public IReadOnlyList<LogRecord> Logs => _logs.Snapshot();

    public IReadOnlyList<Activity> Activities => _activities.Snapshot();

    public IReadOnlyList<Metric> Metrics => _metrics.Snapshot();

    public IServiceProvider Services => _host.Services;

    public ILoggerFactory Loggers => _host.Services.GetRequiredService<ILoggerFactory>();

    public static async Task<TelemetryTestHost> StartAsync(
        string serviceName = ServiceNames.Api,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool diagnostics = false)
    {
        var logs = new SignalSnapshot<LogRecord>();
        var metrics = new SignalSnapshot<Metric>();
        var activities = new UnitActivityCapture();
        var units = new ActivitySource(UnitSourceName);
        var unitListener = ListenToUnits();
        ActivitySource.AddActivityListener(unitListener);

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
                .WithTracing(tracing => tracing.AddProcessor(
                    new SimpleActivityExportProcessor(new UnitScopedActivityExporter(activities)))),
            logging => logging.AddInMemoryExporter(logs));

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new TelemetryTestHost(host, logs, activities, metrics, units, unitListener);
    }

    /// <summary>
    /// Opens the unit of work this case's telemetry belongs to: a root activity on the
    /// capture's own source, so every span started while the handle is alive shares its
    /// trace and the capture keeps those spans and no other. The current activity is
    /// cleared first, so two units alive at once are two traces and never one nested in
    /// the other. The root itself is never exported: the product's pipeline subscribes
    /// to the product's own source only.
    /// </summary>
    public CaptureUnit BeginUnit()
    {
        Activity.Current = null;
        var root = _units.StartActivity("unit-of-work")
            ?? throw new InvalidOperationException("The capture's own source has no listener, so the unit of work could not be opened.");
        _activities.Open(root.TraceId);
        return new CaptureUnit(root, _activities);
    }

    /// <summary>Collects the metric readers now, so the capture and the in-memory exporter have both exported.</summary>
    public void CollectMetrics() => _host.Services.GetRequiredService<MeterProvider>().ForceFlush();

    /// <summary>Reads the capture of a host that has the development diagnostics on.</summary>
    public TelemetryCaptureBuffer Capture => _host.Services.GetRequiredService<TelemetryCaptureBuffer>();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TestContext.Current.CancellationToken);
        _host.Dispose();
        _unitListener.Dispose();
        _units.Dispose();
    }

    private static ActivityListener ListenToUnits()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, UnitSourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        return listener;
    }
}

/// <summary>
/// One unit of work opened on a telemetry host: the trace the capture keeps, ended
/// when the handle is disposed. Ending the unit stops accepting spans, so no capture
/// outlives it.
/// </summary>
internal sealed class CaptureUnit(Activity root, UnitActivityCapture capture) : IDisposable
{
    private bool ended;

    /// <summary>The trace every span of this unit belongs to.</summary>
    public ActivityTraceId TraceId => root.TraceId;

    public void Dispose()
    {
        if (ended)
        {
            return;
        }

        ended = true;
        capture.Close();
        root.Dispose();
    }
}

/// <summary>
/// Keeps the spans of the unit of work a case opened: the exporter hands over every
/// span the process's trace pipeline produced, and only those inside the open unit's
/// trace are kept. Writes arrive from whichever thread ended a span; a read returns a
/// snapshot taken under the same lock, so a case never enumerates a list that is being
/// appended.
/// </summary>
internal sealed class UnitActivityCapture
{
    private readonly Lock _gate = new();
    private readonly List<Activity> _spans = [];
    private ActivityTraceId? _unit;

    /// <summary>Opens the unit whose trace the capture keeps.</summary>
    public void Open(ActivityTraceId traceId)
    {
        lock (_gate)
        {
            _unit = traceId;
        }
    }

    /// <summary>Ends the unit: every span of it that arrived is kept, and none is accepted afterwards.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _unit = null;
        }
    }

    /// <summary>Keeps <paramref name="activity"/> when it belongs to the open unit, and drops it otherwise.</summary>
    public void Accept(Activity activity)
    {
        lock (_gate)
        {
            if (_unit is { } unit && activity.TraceId == unit)
            {
                _spans.Add(activity);
            }
        }
    }

    /// <summary>The spans kept so far, as a value no other thread can change.</summary>
    public IReadOnlyList<Activity> Snapshot()
    {
        lock (_gate)
        {
            return [.. _spans];
        }
    }
}

/// <summary>
/// Hands each finished span to the capture, which keeps the ones of the unit the case
/// opened. The filter sits at export, so a read has one meaning: the spans the
/// pipeline produced for that unit.
/// </summary>
internal sealed class UnitScopedActivityExporter(UnitActivityCapture capture) : BaseExporter<Activity>
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            capture.Accept(activity);
        }

        return ExportResult.Success;
    }
}

/// <summary>
/// The signals one exporter produced, kept for a case to read. The exporter appends
/// from whatever thread produced the signal, and a read returns a snapshot taken under
/// the same lock, so a case never enumerates a collection another thread is writing.
/// </summary>
internal sealed class SignalSnapshot<T> : ICollection<T>
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

    /// <summary>The signals kept so far, as a value no other thread can change.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }
}
