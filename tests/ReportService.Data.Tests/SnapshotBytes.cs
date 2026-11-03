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

    /// <summary>
    /// A document the caller assembles from raw property text, so a case can put the
    /// header after the items or the version last without writing the bytes by hand.
    /// </summary>
    public static byte[] DocumentOf(params string[] rawProperties) =>
        Utf8("{" + string.Join(',', rawProperties) + "}");

    /// <summary>The raw records of a document of <paramref name="count"/> ordinary records.</summary>
    public static string[] Records(int count) =>
        [.. Enumerable.Range(1, count).Select(Record)];

    /// <summary>
    /// The same object text with <paramref name="member"/> repeated as its last member,
    /// so a repeated member is one byte sequence per case.
    /// </summary>
    public static string Duplicated(string json, string member)
    {
        var text = json.Trim();
        if (text.Length < 2 || text[0] != '{' || text[^1] != '}')
        {
            throw new ArgumentException("A repeated member is added to an object.", nameof(json));
        }

        var body = text[1..^1].TrimEnd();
        var repeated = "\"" + member + "\":null";
        return body.Length == 0 ? "{" + repeated + "}" : "{" + body + "," + repeated + "}";
    }

    /// <summary>The same bytes inside one complete gzip stream.</summary>
    public static byte[] Gzip(byte[] document)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(document);
        }

        return compressed.ToArray();
    }

    /// <summary>The byte index of the <paramref name="occurrence"/>-th appearance of <paramref name="marker"/>.</summary>
    public static int OffsetOf(byte[] document, string marker, int occurrence = 0)
    {
        var bytes = Utf8(marker);
        var from = 0;
        for (var found = 0; ; found++)
        {
            var at = document.AsSpan(from).IndexOf(bytes);
            if (at < 0)
            {
                throw new InvalidOperationException($"the document holds no occurrence {occurrence} of {marker}.");
            }

            at += from;
            if (found == occurrence)
            {
                return at;
            }

            from = at + 1;
        }
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
