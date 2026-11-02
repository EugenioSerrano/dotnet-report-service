using System.Text;
using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// Snapshot documents and small helpers the reader's cases are written with. Every
/// document is built here so a case says what its input is, in the profiled order
/// of the three root properties, and never by hand in the middle of an assertion.
/// </summary>
internal static class SnapshotBytes
{
    public const string ValidHeader = """{"title":"Monthly statement","currency":"EUR"}""";

    /// <summary>The UTF-8 bytes of <paramref name="text"/>.</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A document with the given raw header and raw records, in profile order.</summary>
    public static byte[] Document(string header = ValidHeader, params string[] records)
    {
        var items = records.Length == 0 ? "[]" : "[" + string.Join(',', records) + "]";
        return Utf8($$"""{"contractVersion":"1.0","header":{{header}},"items":{{items}}}""");
    }

    /// <summary>A record with an ordinary shaped body, numbered by <paramref name="ordinal"/>.</summary>
    public static string Record(int ordinal) =>
        $$"""{"sequence":{{ordinal}},"description":"row {{ordinal}}","amount":{{ordinal}}.25}""";

    /// <summary>A record whose description is <paramref name="bytes"/> ASCII bytes long.</summary>
    public static string TallRecord(int ordinal, int bytes) =>
        $$"""{"sequence":{{ordinal}},"description":"{{new string('x', bytes)}}","amount":1}""";

    /// <summary>
    /// An object of many short members, at least <paramref name="bytes"/> characters long,
    /// and with no single token anywhere near a token limit: the case that reaches a
    /// value's own limit rather than the buffer's.
    /// </summary>
    public static string WideObject(int bytes)
    {
        var builder = new System.Text.StringBuilder("{");
        var index = 0;
        while (builder.Length < bytes)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append("\"f").Append(index).Append("\":0");
            index++;
        }

        return builder.Append('}').ToString();
    }


    /// <summary>Reads the whole sequence, so a case can assert on it as a list.</summary>
    public static async Task<List<RawRecord>> ReadAllAsync(byte[] document, CancellationToken cancellationToken)
    {
        await using var source = new MemoryStream(document, writable: false);
        var read = new List<RawRecord>();
        await foreach (var record in HeaderItemsReader.ReadAsync(source, cancellationToken))
        {
            read.Add(record);
        }

        return read;
    }

    /// <summary>The text of <paramref name="record"/>, for assertions about copied bytes.</summary>
    public static string Text(RawRecord record) => Encoding.UTF8.GetString(record.Json.Span);
}
