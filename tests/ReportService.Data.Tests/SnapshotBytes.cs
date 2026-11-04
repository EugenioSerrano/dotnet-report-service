using System.IO.Compression;
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

    /// <summary>The raw records of a document of <paramref name="count"/> ordinary records.</summary>
    public static string[] Records(int count) => [.. Enumerable.Range(1, count).Select(Record)];

    /// <summary>
    /// A document assembled from raw property text, in the order the cases hand them in, so
    /// a case can put the header after the items or the version last without writing bytes
    /// by hand in the middle of an assertion.
    /// </summary>
    public static byte[] DocumentOf(params string[] rawProperties) =>
        Utf8("{" + string.Join(',', rawProperties) + "}");

    /// <summary>
    /// The object text with <paramref name="member"/> repeated as its last member, carrying
    /// <paramref name="rawValue"/>, so the case holds one byte sequence of its own.
    /// </summary>
    public static string Duplicated(string json, string member, string rawValue) =>
        json[..^1] + ",\"" + member + "\":" + rawValue + "}";

    /// <summary>The same bytes inside one complete gzip stream, as an upload may hold them.</summary>
    public static byte[] Gzip(byte[] document)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(document);
        }

        return compressed.ToArray();
    }

    /// <summary>
    /// The byte index of the <paramref name="occurrence"/>-th occurrence of
    /// <paramref name="marker"/> in <paramref name="document"/>, so a case computes the
    /// offset it expects from its own bytes instead of writing a constant by hand.
    /// </summary>
    public static long OffsetOf(byte[] document, string marker, int occurrence = 1)
    {
        var needle = Utf8(marker);
        var search = 0;
        for (var found = 1; ; found++)
        {
            var at = document.AsSpan(search).IndexOf(needle);
            if (at < 0)
            {
                throw new InvalidOperationException(
                    $"the document holds no occurrence {occurrence} of '{marker}'");
            }

            var index = search + at;
            if (found == occurrence)
            {
                return index;
            }

            search = index + needle.Length;
        }
    }

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

    /// <summary>The text of <paramref name="document"/>, for cases that cut or extend it.</summary>
    public static string Text(byte[] document) => Encoding.UTF8.GetString(document);
}
