using System.Globalization;
using System.IO.Compression;
using System.Text;
using StreamingDataSpike.Measuring;

namespace StreamingDataSpike;

/// <summary>
/// Writes the hostile inputs H1–H14 and holds the outcome each one must give.
/// Every file is a valid envelope except for the one defect it carries.
/// </summary>
internal static class HostileInputs
{
    private const int MiB = 1_048_576;

    /// <summary>The expected outcome of every hostile input.</summary>
    public static readonly HostileCase[] Cases =
    [
        new("H1", "h01-header-too-large.json", [], 3, "QUOTA_EXCEEDED", "limit=header", 0),
        new("H2", "h02-record-too-large.json", [], 3, "RECORD_TOO_LARGE", "limit=record", 2),
        new("H3", "h03-token-too-large.json", ["--token-limit", "65536"], 3, "QUOTA_EXCEEDED", "limit=token", 2),
        new("H4", "h04-too-deep.json", [], 3, "QUOTA_EXCEEDED", "limit=depth", 2),
        new("H5", "h05-huge-string.json.gz", [], 3, "RECORD_TOO_LARGE", "limit=record", 1),
        new("H6", "h06-large-input.json.gz", ["--input-limit", "33554432"], 3, "QUOTA_EXCEEDED", "limit=input-bytes", null),
        new("H7", "h07-truncated.json", [], 3, "DATA_INVALID", "reason=truncated", null),
        new("H8", "h08-trailing-content.json", [], 3, "DATA_INVALID", "reason=trailing-content", null),
        new("H9", "h09-items-before-header.json", [], 3, "DATA_INVALID", "reason=header-not-first", null),
        new("H10", "h10-duplicate-property.json", [], 3, "DATA_INVALID", "reason=duplicate-property", 2),
        new("H11", "h11-invalid-utf8.json", [], 3, "DATA_INVALID", "reason=malformed", 2),
        new("H12", "h12-long-whitespace.json", [], null, null, null, null),
        new("H13", "h13-contract-version.json", [], 3, "DATA_INVALID", "reason=contract-version", 0),
        new("H14", "h14-totals-mismatch.json", [], 3, "DATA_INVALID", "reason=totals-mismatch", null),
    ];

    public static async Task<int> WriteAsync(Arguments arguments)
    {
        var folder = arguments.Required("--output");
        var from = arguments.Option("--from");
        arguments.EnsureAllUsed(0);
        await WriteAllAsync(folder, from).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Writes every hostile input into <paramref name="folder"/>; H7 needs the volume 100 k file.</summary>
    public static async Task WriteAllAsync(string folder, string? volume100k)
    {
        Directory.CreateDirectory(folder);
        var big = new string('a', MiB + (MiB / 2));

        Write(folder, "h01-header-too-large.json", Envelope(Header(3, 3.00m, "\"note\":\"" + big + "\""), Items(3)));
        Write(folder, "h02-record-too-large.json", Envelope(Header(3, 3.00m), Items(3, 2, "\"description\":\"" + big + "\"")));
        Write(folder, "h03-token-too-large.json", Envelope(Header(3, 3.00m), Items(3, 2, "\"description\":\"" + new string('b', 100 * 1024) + "\"")));
        Write(folder, "h04-too-deep.json", Envelope(Header(3, 3.00m), Items(3, 2, "\"nest\":" + new string('[', 70) + new string(']', 70))));
        await WriteHugeStringAsync(Path.Combine(folder, "h05-huge-string.json.gz")).ConfigureAwait(false);
        await WriteLargeInputAsync(Path.Combine(folder, "h06-large-input.json.gz")).ConfigureAwait(false);
        if (volume100k is not null)
        {
            await WriteTruncatedAsync(volume100k, Path.Combine(folder, "h07-truncated.json")).ConfigureAwait(false);
        }

        Write(folder, "h08-trailing-content.json", Envelope(Header(3, 3.00m), Items(3)) + "{\"x\":1}\n");
        Write(folder, "h09-items-before-header.json", "{\"contractVersion\":\"1.0\",\"items\":[" + Items(3) + "],\"header\":" + Header(3, 3.00m) + "}\n");
        Write(folder, "h10-duplicate-property.json", Envelope(Header(3, 3.00m), Items(3, 2, "\"amount\":1.00")));
        WriteInvalidUtf8(Path.Combine(folder, "h11-invalid-utf8.json"));
        Write(folder, "h12-long-whitespace.json", Envelope(Header(3, 3.00m), Items(1) + "," + new string(' ', 4 * MiB) + Items(2, start: 2)));
        Write(folder, "h13-contract-version.json", Envelope(Header(3, 3.00m), Items(3)).Replace("\"contractVersion\":\"1.0\"", "\"contractVersion\":\"2.0\"", StringComparison.Ordinal));
        Write(folder, "h14-totals-mismatch.json", Envelope(Header(4, 3.00m), Items(3)));
    }

    private static string Envelope(string header, string items) =>
        "{\"contractVersion\":\"1.0\",\"header\":" + header + ",\"items\":[" + items + "]}\n";

    private static string Header(long records, decimal amount, string? extra = null) =>
        "{\"datasetId\":\"hostile\",\"reportTitle\":\"Hostile input\",\"generatedAt\":\"2026-10-01T00:00:00Z\",\"currency\":\"ARS\","
        + (extra is null ? string.Empty : extra + ",")
        + "\"totals\":{\"recordCount\":" + records.ToString(CultureInfo.InvariantCulture) + ",\"amount\":" + amount.ToString(CultureInfo.InvariantCulture) + "}}";

    /// <summary>Records with amount 1.00; record <paramref name="special"/> gets the extra property.</summary>
    private static string Items(int count, int special = 0, string? extra = null, int start = 1)
    {
        var builder = new StringBuilder();
        for (var i = start; i < start + count; i++)
        {
            if (i > start)
            {
                builder.Append(',');
            }

            builder.Append(CultureInfo.InvariantCulture, $"{{\"sequence\":{i},\"description\":\"Item {i}\",\"amount\":1.00");
            if (i == special && extra is not null)
            {
                builder.Append(',').Append(extra);
            }

            builder.Append('}');
        }

        return builder.ToString();
    }

    private static void Write(string folder, string name, string text)
    {
        using var stream = new FileStream(Path.Combine(folder, name), FileMode.Create, FileAccess.Write);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes);
    }

    private static void WriteInvalidUtf8(string path)
    {
        var text = Envelope(Header(3, 3.00m), Items(3, 2, "\"note\":\"abc\u0001def\""));
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var index = Array.IndexOf(bytes, (byte)0x01);
        bytes[index] = 0xFF; // never valid in UTF-8
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>H5: record 1 is one 512 MiB string, written through gzip (a few hundred KiB on disk).</summary>
    private static async Task WriteHugeStringAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65_536);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        var utf8 = new UTF8Encoding(false);
        await gzip.WriteAsync(utf8.GetBytes("{\"contractVersion\":\"1.0\",\"header\":" + Header(1, 1.00m) + ",\"items\":[{\"sequence\":1,\"description\":\"")).ConfigureAwait(false);
        var chunk = new byte[MiB];
        Array.Fill(chunk, (byte)'z');
        for (var i = 0; i < 512; i++)
        {
            await gzip.WriteAsync(chunk).ConfigureAwait(false);
        }

        await gzip.WriteAsync(utf8.GetBytes("\",\"amount\":1.00}]}\n")).ConfigureAwait(false);
    }

    /// <summary>H6: a valid envelope of about 100 MiB uncompressed, gzip.</summary>
    private static async Task WriteLargeInputAsync(string path)
    {
        const int Count = 1_000_000;
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65_536);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var writer = new StreamWriter(gzip, new UTF8Encoding(false), 65_536);
        await writer.WriteAsync("{\"contractVersion\":\"1.0\",\"header\":" + Header(Count, Count * 1.00m) + ",\"items\":[").ConfigureAwait(false);
        for (var i = 1; i <= Count; i++)
        {
            if (i > 1)
            {
                await writer.WriteAsync(',').ConfigureAwait(false);
            }

            await writer.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"{{\"sequence\":{i},\"description\":\"Large input filler record number {i:D7} with some text\",\"amount\":1.00}}")).ConfigureAwait(false);
        }

        await writer.WriteAsync("]}\n").ConfigureAwait(false);
    }

    /// <summary>H7: the volume 100 k file without its last 64 bytes.</summary>
    private static async Task WriteTruncatedAsync(string source, string path)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65_536);
        var remaining = input.Length - 64;
        var buffer = new byte[65_536];
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remaining -= read;
        }
    }
}

/// <summary>One hostile input and its expected outcome (H12 accepts either of two outcomes).</summary>
internal sealed record HostileCase(string Id, string File, string[] Options, int? ExitCode, string? Code, string? Detail, long? Ordinal);

/// <summary>Formats a hostile run for the results file.</summary>
internal static class HostileReport
{
    public static string Line(HostileCase hostile, ChildResult child, bool pass, string observed) =>
        "{\"phase\":\"hostile\",\"id\":\"" + hostile.Id + "\",\"pass\":" + (pass ? "true" : "false")
        + ",\"exitCode\":" + child.ExitCode.ToString(CultureInfo.InvariantCulture)
        + ",\"observed\":\"" + observed + "\",\"result\":" + (string.IsNullOrWhiteSpace(child.StandardOutput) ? "null" : child.StandardOutput.Trim()) + "}";
}
