using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StreamingDataSpike.Parsing;

namespace StreamingDataSpike.Measuring;

/// <summary>Writes one JSON object per result line (file names only, never directory paths).</summary>
internal sealed class ResultLine : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly Utf8JsonWriter _writer;

    public ResultLine(string command, string? file)
    {
        _writer = new Utf8JsonWriter(_stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        _writer.WriteStartObject();
        _writer.WriteString("command", command);
        if (file is not null)
        {
            _writer.WriteString("file", Path.GetFileName(file));
        }
    }

    public ResultLine Add(string name, string? value)
    {
        if (value is null)
        {
            _writer.WriteNull(name);
        }
        else
        {
            _writer.WriteString(name, value);
        }

        return this;
    }

    public ResultLine Add(string name, long value)
    {
        _writer.WriteNumber(name, value);
        return this;
    }

    public ResultLine Add(string name, long? value)
    {
        if (value is { } number)
        {
            _writer.WriteNumber(name, number);
        }
        else
        {
            _writer.WriteNull(name);
        }

        return this;
    }

    public ResultLine Add(string name, decimal value)
    {
        _writer.WriteNumber(name, value);
        return this;
    }

    public ResultLine Add(string name, double value)
    {
        _writer.WriteNumber(name, Math.Round(value, 6));
        return this;
    }

    public ResultLine Add(string name, bool value)
    {
        _writer.WriteBoolean(name, value);
        return this;
    }

    /// <summary>Adds the common memory figures (Chromium is not used here, temporary disk is zero).</summary>
    public ResultLine AddReading(MeterReading reading, long records)
    {
        Add("managedHeapPeakBytes", reading.ManagedHeapPeakBytes);
        Add("processPeakRssBytes", reading.ProcessPeakRssBytes);
        Add("chromiumTreePeakRssBytes", (long?)null);
        Add("tempDiskBytes", 0L);
        Add("peakWorkingSet64Bytes", reading.PeakWorkingSet64Bytes);
        Add("vmHwmBytes", reading.VmHwmBytes);
        Add("totalAllocatedBytes", reading.TotalAllocatedBytes);
        Add("gen0", reading.Gen0Collections);
        Add("gen1", reading.Gen1Collections);
        Add("gen2", reading.Gen2Collections);
        Add("gcMode", reading.GcMode);
        Add("totalAvailableMemoryBytes", reading.TotalAvailableMemoryBytes);
        Add("seconds", reading.Seconds);
        Add("recordsPerSecond", reading.Seconds > 0 ? Math.Round(records / reading.Seconds) : 0);
        Add("os", reading.Os);
        Add("runtime", reading.Runtime);
        return this;
    }

    public ResultLine AddError(EnvelopeException exception)
    {
        _writer.WriteStartObject("error");
        _writer.WriteString("code", exception.Code);
        if (exception.Limit is not null)
        {
            _writer.WriteString("limit", exception.Limit);
        }

        if (exception.Reason is not null)
        {
            _writer.WriteString("reason", exception.Reason);
        }

        _writer.WriteNumber("ordinal", exception.RecordOrdinal);
        _writer.WriteNumber("offset", exception.ByteOffset);
        _writer.WriteEndObject();
        return this;
    }

    public override string ToString()
    {
        _writer.WriteEndObject();
        _writer.Flush();
        return Encoding.UTF8.GetString(_stream.ToArray());
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}
