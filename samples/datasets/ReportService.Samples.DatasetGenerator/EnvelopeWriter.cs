using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ReportService.Samples.DatasetGenerator;

/// <summary>Statistics of one written dataset file.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Records">Number of records written.</param>
/// <param name="Groups">Number of groups written (0 for the simple dataset).</param>
/// <param name="Bytes">File size in bytes.</param>
/// <param name="Sha256">SHA-256 of the file, lowercase hexadecimal.</param>
/// <param name="Seconds">Time spent writing and hashing.</param>
internal sealed record GeneratedFile(string Path, long Records, int Groups, long Bytes, string Sha256, double Seconds);

/// <summary>
/// Writes a dataset in the <c>header-items-v1</c> envelope in two passes:
/// the first pass runs the record sequence to compute the header totals, the
/// second runs it again from the start and writes. No records are held in
/// memory; the writer is flushed every 64 KiB.
/// </summary>
internal static class EnvelopeWriter
{
    private const int FlushThreshold = 64 * 1024;
    private const int FileBufferSize = 1 << 20;

    /// <summary>Writes the dataset to <paramref name="path"/> and returns its statistics.</summary>
    public static GeneratedFile Write(DatasetDefinition definition, ulong seed, long count, string path, bool indented)
    {
        var stopwatch = Stopwatch.StartNew();

        var totals = new RecordSource(definition, seed, count);
        var records = 0L;
        var amount = RecordSource.Scaled(0, 2);
        while (totals.MoveNext())
        {
            records++;
            amount += totals.Current.Amount;
        }

        var groups = totals.GroupCount;
        var source = new RecordSource(definition, seed, count);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize))
        {
            using (var writer = new Utf8JsonWriter(stream, Options(indented)))
            {
                writer.WriteStartObject();
                writer.WriteString("contractVersion", "1.0");
                WriteHeader(writer, source, records, groups, amount);
                writer.WriteStartArray("items");
                while (source.MoveNext())
                {
                    WriteItem(writer, definition.Kind, source.Current);
                    if (writer.BytesPending >= FlushThreshold)
                    {
                        writer.Flush();
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }

            stream.WriteByte((byte)'\n');
        }

        var sha256 = Hashes.OfFile(path);
        var bytes = new FileInfo(path).Length;
        return new GeneratedFile(path, records, groups, bytes, sha256, stopwatch.Elapsed.TotalSeconds);
    }

    private static JsonWriterOptions Options(bool indented) => new()
    {
        Indented = indented,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void WriteHeader(Utf8JsonWriter writer, RecordSource source, long records, int groups, decimal amount)
    {
        writer.WriteStartObject("header");
        writer.WriteString("datasetId", source.Definition.DatasetId);
        writer.WriteString("reportTitle", source.Definition.ReportTitle);
        writer.WriteString("generatedAt", DatasetDefinitions.GeneratedAt);
        writer.WriteNumber("seed", source.Seed);
        writer.WriteString("currency", DatasetDefinitions.Currency);
        writer.WriteStartObject("customer");
        writer.WriteString("id", source.CustomerId);
        writer.WriteString("name", source.CustomerName);
        writer.WriteEndObject();
        writer.WriteStartObject("totals");
        writer.WriteNumber("recordCount", records);
        if (source.Definition.HasGroups)
        {
            writer.WriteNumber("groupCount", groups);
        }

        WriteDecimal(writer, "amount", amount);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteItem(Utf8JsonWriter writer, DatasetKind kind, DatasetRecord record)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", record.Sequence);
        switch (kind)
        {
            case DatasetKind.Simple:
                writer.WriteString("description", record.Description);
                writer.WriteNumber("quantity", record.WholeQuantity);
                WriteDecimal(writer, "unitPrice", record.UnitPrice);
                WriteDecimal(writer, "amount", record.Amount);
                break;
            case DatasetKind.Grouped:
                writer.WriteString("groupId", record.GroupId);
                writer.WriteString("groupName", record.GroupName);
                writer.WriteString("date", record.Date);
                writer.WriteString("description", record.Description);
                WriteDecimal(writer, "amount", record.Amount);
                break;
            default:
                writer.WriteString("groupId", record.GroupId);
                writer.WriteString("accountId", record.AccountId);
                writer.WriteString("description", record.Description);
                WriteDecimal(writer, "quantity", record.PreciseQuantity);
                WriteDecimal(writer, "amount", record.Amount);
                writer.WriteNumber("reference", record.Reference);
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteDecimal(Utf8JsonWriter writer, string name, decimal value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(value.ToString(CultureInfo.InvariantCulture), skipInputValidation: true);
    }
}
