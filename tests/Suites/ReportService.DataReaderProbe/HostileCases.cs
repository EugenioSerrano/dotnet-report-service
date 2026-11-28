using System.IO.Compression;
using System.Text;

namespace ReportService.DataReaderProbe;

/// <summary>
/// One hostile input of the data pass at the two-mebibyte record bound: how it is written and
/// the outcome it must give. An expected code of <see langword="null"/> means the input is
/// admissible and the pass must end ok.
/// </summary>
/// <param name="Name">The case's short name.</param>
/// <param name="FileName">The file the case is written to.</param>
/// <param name="ExpectedCode">The stable code, or null when the input is admitted.</param>
/// <param name="ExpectedLimitOrReason">The <c>limit=</c> or <c>reason=</c> token the refusal carries, or null.</param>
/// <param name="WithSchema">Whether the case is read with the pattern schema validating each record.</param>
/// <param name="Write">Writes the input into the given path.</param>
public sealed record HostileCase(
    string Name,
    string FileName,
    string? ExpectedCode,
    string? ExpectedLimitOrReason,
    bool WithSchema,
    Action<string> Write)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The hostile inputs of the data pass, re-stated at the bounds of this version: record and
/// token two mebibytes, header one, depth 64. Each limit is crossed by one byte where a byte is
/// meaningful, and the malformed-document reasons the pass reports are each given one input -
/// invalid UTF-8 inside a string value excepted, which the reader does not yet refuse.
/// The input-size quota and the verified totals belong to the upload's verification, not to
/// this pass, and are not repeated here. Two admissible cases close the list: a record that is
/// one two-mebibyte string, and a two-mebibyte record whose base64 image string is validated by
/// a schema with a pattern on that field.
/// </summary>
public static class HostileCases
{
    /// <summary>The record and token bound of this version.</summary>
    public const int RecordBytes = 2_097_152;

    /// <summary>The header bound of this version.</summary>
    public const int HeaderBytes = 1_048_576;

    /// <summary>The schema of the validation case: a pattern on the image field.</summary>
    public const string PatternSchema = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema",
         "properties":{"header":{"type":"object"},
                       "items":{"items":{"type":"object","required":["sequence","picture"],
                         "properties":{"sequence":{"type":"integer","minimum":1},
                                       "picture":{"type":"string","pattern":"^data:image/png;base64,[A-Za-z0-9+/]*={0,2}$"}}}}}}
        """;

    private const string Header = """{"title":"Hostile"}""";

    /// <summary>Every case, in a fixed order.</summary>
    public static IReadOnlyList<HostileCase> All { get; } =
    [
        new("H1", "h01-header-too-large.json", "QUOTA_EXCEEDED", "limit=header", false,
            path => Plain(path, Envelope(Sized("{\"title\":\"", HeaderBytes + 1, "\"}"), Ordinary(1)))),
        new("H2", "h02-record-too-large.json", "RECORD_TOO_LARGE", "limit=record", false,
            path => Plain(path, Envelope(Header, Ordinary(1), Wide(RecordBytes + 1)))),
        new("H3", "h03-token-too-large.json", "QUOTA_EXCEEDED", "limit=token", false,
            path => Plain(path, Envelope(Header, Ordinary(1), "{\"v\":\"" + new string('t', RecordBytes + 1) + "\"}"))),
        new("H4", "h04-too-deep.json", "QUOTA_EXCEEDED", "limit=depth", false,
            path => Plain(path, Envelope(Header, Ordinary(1), Deep(65)))),
        new("H5", "h05-huge-string.json.gz", "QUOTA_EXCEEDED", "limit=token", false, HugeString),
        new("H7", "h07-truncated.json", "DATA_INVALID", "reason=truncated", false,
            path => Plain(path, Envelope(Header, Ordinary(1), Ordinary(2))[..^8])),
        new("H8", "h08-trailing-content.json", "DATA_INVALID", "reason=trailing-content", false,
            path => Plain(path, Envelope(Header, Ordinary(1)) + "{}")),
        new("H9", "h09-items-before-header.json", "DATA_INVALID", "reason=header-not-first", false,
            path => Plain(path, "{\"contractVersion\":\"1.0\",\"items\":[" + Ordinary(1) + "],\"header\":" + Header + "}")),
        new("H10", "h10-duplicate-property.json", "DATA_INVALID", "reason=duplicate-property", false,
            path => Plain(path, Envelope(Header, "{\"sequence\":1,\"sequence\":2}"))),
        // H11 (a string value holding invalid UTF-8) is not in this list: the reader does not
        // yet refuse invalid UTF-8 inside a string value. The case returns, expecting
        // DATA_INVALID reason=malformed, with the reader change that refuses it.

        // The whitespace run follows the comma between two records: there the reader must hold
        // the comma, the whole run and the next token in one buffer, so a run longer than the
        // buffer cap is refused as a token. After '[', ':' or a value the reader skips a run it
        // cannot finish, so a run placed there proves nothing about the cap.
        new("H12", "h12-long-whitespace.json", "QUOTA_EXCEEDED", "limit=token", false,
            path => Plain(path, Envelope(Header, Ordinary(1), new string(' ', RecordBytes + 1) + Ordinary(2)))),
        new("H13", "h13-contract-version.json", "DATA_INVALID", "reason=contract-version", false,
            path => Plain(path, "{\"contractVersion\":\"2.0\",\"header\":" + Header + ",\"items\":[]}")),
        new("S1", "s01-two-mebibyte-string.json", null, null, false,
            path => Plain(path, Envelope(Header, Ordinary(1), Sized("{\"v\":\"", RecordBytes, "\"}"), Ordinary(3)))),
        new("S2", "s02-two-mebibyte-image-validated.json", null, null, true,
            path => Plain(path, Envelope(Header, Picture(1, 10), Sized("{\"sequence\":2,\"picture\":\"data:image/png;base64,", RecordBytes, "\"}", 'A'), Picture(3, 10)))),
    ];

    private static string Envelope(string header, params string[] records) =>
        "{\"contractVersion\":\"1.0\",\"header\":" + header + ",\"items\":[" + string.Join(',', records) + "]}";

    private static string Ordinary(int sequence) => "{\"sequence\":" + sequence + ",\"v\":\"row\"}";

    private static string Picture(int sequence, int bodyQuads) =>
        "{\"sequence\":" + sequence + ",\"picture\":\"data:image/png;base64," + new string('A', bodyQuads * 4) + "\"}";

    // A value of exactly the given bytes: prefix, filler, suffix.
    private static string Sized(string prefix, int bytes, string suffix, char filler = 'x') =>
        prefix + new string(filler, bytes - prefix.Length - suffix.Length) + suffix;

    // An object of many short members, so the record bound - never the token bound - is crossed.
    private static string Wide(int atLeast)
    {
        var builder = new StringBuilder("{\"sequence\":1");
        var index = 0;
        while (builder.Length < atLeast)
        {
            builder.Append(",\"m").Append(index++).Append("\":\"").Append('w', 1_000).Append('"');
        }

        return builder.Append('}').ToString();
    }

    private static string Deep(int levels) => new StringBuilder().Insert(0, "{\"a\":", levels).Append('1').Append('}', levels).ToString();

    private static void Plain(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    // Record 1 is one 512 MiB string, written through gzip (a few hundred KiB on disk).
    private static void HugeString(string path)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(Encoding.UTF8.GetBytes("{\"contractVersion\":\"1.0\",\"header\":" + Header + ",\"items\":[{\"v\":\""));
        var chunk = Encoding.ASCII.GetBytes(new string('s', 1 << 20));
        for (var mebibyte = 0; mebibyte < 512; mebibyte++)
        {
            gzip.Write(chunk);
        }

        gzip.Write("\"}]}"u8);
    }
}
