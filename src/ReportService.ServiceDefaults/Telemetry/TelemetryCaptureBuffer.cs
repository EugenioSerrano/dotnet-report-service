using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>One instrument a process declared, whether or not a point of it was retained.</summary>
internal sealed record CapturedInstrument(string Name, string Meter, string Type, string Unit);

/// <summary>One metric point as the pipeline handed it to the capture: name, type, unit, value, tags and times.</summary>
internal sealed record CapturedPoint(
    string Instrument,
    string Type,
    string Unit,
    double? Value,
    bool? Monotonic,
    long? Count,
    double? Sum,
    double? Min,
    double? Max,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    IReadOnlyList<KeyValuePair<string, string?>> Tags);

/// <summary>One read of the capture, rendered on request.</summary>
internal sealed record CapturedMetrics(
    IReadOnlyDictionary<string, string?> Resource,
    int ExportIntervalMilliseconds,
    IReadOnlyList<CapturedInstrument> Instruments,
    IReadOnlyList<CapturedPoint> Points,
    long OmittedPoints)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>
    /// Renders the read as the diagnostics body. Points are written until the
    /// body reaches its cap; the rest are counted in <c>omittedPoints</c>, so the
    /// response never grows with the number of measurements.
    /// </summary>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var written = 0;
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            WriteService(writer);
            writer.WriteNumber("exportIntervalMilliseconds", ExportIntervalMilliseconds);
            writer.WriteStartArray("instruments");
            foreach (var instrument in Instruments)
            {
                writer.WriteStartObject();
                writer.WriteString("name", instrument.Name);
                writer.WriteString("meter", instrument.Meter);
                writer.WriteString("type", instrument.Type);
                writer.WriteString("unit", instrument.Unit);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("points");
            foreach (var point in Points)
            {
                if (writer.BytesCommitted + writer.BytesPending > TelemetryCaptureBuffer.MaxBodyBytes - TelemetryCaptureBuffer.EnvelopeReserveBytes)
                {
                    break;
                }

                WritePoint(writer, point);
                written++;
            }

            writer.WriteEndArray();
            writer.WriteNumber("omittedPoints", OmittedPoints + (Points.Count - written));
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void WriteService(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("service");
        writer.WriteString("name", Attribute("service.name"));
        writer.WriteString("version", Attribute("service.version"));
        writer.WriteString("instanceId", Attribute("service.instance.id"));
        writer.WriteString("environment", Attribute("deployment.environment"));
        writer.WriteEndObject();
    }

    private string? Attribute(string name) => Resource.TryGetValue(name, out var value) ? value : null;

    private static void WritePoint(Utf8JsonWriter writer, CapturedPoint point)
    {
        writer.WriteStartObject();
        writer.WriteString("instrument", point.Instrument);
        writer.WriteString("type", point.Type);
        writer.WriteString("unit", point.Unit);
        if (point.Value is { } value)
        {
            writer.WriteNumber("value", value);
        }

        if (point.Monotonic is { } monotonic)
        {
            writer.WriteBoolean("monotonic", monotonic);
        }

        if (point.Count is { } count)
        {
            writer.WriteNumber("count", count);
        }

        if (point.Sum is { } sum)
        {
            writer.WriteNumber("sum", sum);
        }

        if (point.Min is { } min)
        {
            writer.WriteNumber("min", min);
        }

        if (point.Max is { } max)
        {
            writer.WriteNumber("max", max);
        }

        writer.WriteString("startTime", point.StartTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("endTime", point.EndTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteStartObject("tags");
        foreach (var tag in point.Tags)
        {
            writer.WriteString(tag.Key, tag.Value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

/// <summary>
/// The capture's fixed-size retention: at most
/// <see cref="Capacity"/> metric points, oldest dropped first, and a response
/// body capped at <see cref="MaxBodyBytes"/>. It only stores what an exporter
/// was handed — it filters, aggregates, renames and re-tags nothing.
/// </summary>
internal sealed class TelemetryCaptureBuffer
{
    /// <summary>The most points the capture retains.</summary>
    public const int Capacity = 2000;

    /// <summary>The largest response body the read endpoint returns.</summary>
    public const int MaxBodyBytes = 512 * 1024;

    /// <summary>What is kept free for the body's own envelope and its omitted count.</summary>
    internal const int EnvelopeReserveBytes = 4096;

    private static readonly IReadOnlyList<KeyValuePair<string, string?>> NoTags = [];

    private readonly object gate = new();
    private readonly HashSet<CapturedInstrument> instruments = [];
    private readonly Queue<CapturedPoint> points = new(Capacity);
    private IReadOnlyDictionary<string, string?> resource = new Dictionary<string, string?>(StringComparer.Ordinal);
    private long dropped;

    /// <summary>The points currently retained.</summary>
    public int RetainedPoints
    {
        get
        {
            lock (gate)
            {
                return points.Count;
            }
        }
    }

    /// <summary>The points dropped because the retention was full.</summary>
    public long OmittedPoints
    {
        get
        {
            lock (gate)
            {
                return dropped;
            }
        }
    }

    /// <summary>The resource attributes of the provider the capture reads, as the process declares them.</summary>
    public void SetResource(IReadOnlyDictionary<string, string?> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        lock (gate)
        {
            resource = attributes;
        }
    }

    /// <summary>Stores what one export handed over, dropping the oldest point when the retention is full.</summary>
    public void Record(IEnumerable<CapturedInstrument> declared, IEnumerable<CapturedPoint> captured)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(captured);
        lock (gate)
        {
            foreach (var instrument in declared)
            {
                instruments.Add(instrument);
            }

            foreach (var point in captured)
            {
                while (points.Count >= Capacity)
                {
                    points.Dequeue();
                    dropped++;
                }

                points.Enqueue(point with { Tags = point.Tags.Count == 0 ? NoTags : point.Tags });
            }
        }
    }

    /// <summary>Takes a consistent read of everything retained.</summary>
    public CapturedMetrics Take(int exportIntervalMilliseconds)
    {
        lock (gate)
        {
            return new CapturedMetrics(
                new Dictionary<string, string?>(resource, StringComparer.Ordinal),
                exportIntervalMilliseconds,
                [.. instruments],
                [.. points],
                dropped);
        }
    }
}
