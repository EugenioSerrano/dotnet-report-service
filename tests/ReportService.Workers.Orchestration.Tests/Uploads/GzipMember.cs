using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Crc32 = ReportService.Workers.Orchestration.Uploads.Crc32;

namespace ReportService.Workers.Orchestration.Tests.Uploads;

/// <summary>
/// Compressed test objects built byte by byte, so every header flag and every
/// damaged form is reachable: the header's optional fields, the smallest
/// possible content, a single flipped byte in the compressed stream, a flipped
/// length or checksum in the trailer, and an object cut at any offset.
/// </summary>
internal static class GzipMember
{
    /// <summary>A member written by the runtime's own compressor.</summary>
    public static byte[] Compress(byte[] content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, level, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    /// <summary>
    /// A member built by hand: the ten-byte header with the flags asked for, the
    /// compressed content, and the trailer that attests it.
    /// </summary>
    public static byte[] Build(
        ReadOnlySpan<byte> content,
        byte flags = 0,
        byte[]? extra = null,
        string? name = null,
        string? comment = null)
    {
        var header = new List<byte> { 0x1f, 0x8b, 0x08, flags, 0, 0, 0, 0, 0x00, 0xff };
        if ((flags & 0x04) != 0)
        {
            var field = extra ?? [];
            header.Add((byte)(field.Length & 0xFF));
            header.Add((byte)(field.Length >> 8));
            header.AddRange(field);
        }

        if ((flags & 0x08) != 0)
        {
            header.AddRange(Encoding.ASCII.GetBytes(name ?? "x"));
            header.Add(0);
        }

        if ((flags & 0x10) != 0)
        {
            header.AddRange(Encoding.ASCII.GetBytes(comment ?? "x"));
            header.Add(0);
        }

        if ((flags & 0x02) != 0)
        {
            var headerCrc = Crc32.Of([.. header]);
            header.Add((byte)(headerCrc & 0xFF));
            header.Add((byte)((headerCrc >> 8) & 0xFF));
        }

        var member = new List<byte>(header);
        member.AddRange(Deflate(content));
        member.AddRange(Trailer(Crc32.Of(content), content.Length));
        return [.. member];
    }

    /// <summary>The smallest possible member: an empty content in twenty bytes.</summary>
    public static byte[] Empty() =>
    [
        0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff,
        0x03, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>The eight-byte trailer of <paramref name="length"/> content with <paramref name="crc32"/>.</summary>
    public static byte[] Trailer(uint crc32, long length)
    {
        var trailer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(0), crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), unchecked((uint)length));
        return trailer;
    }

    /// <summary>The stored bytes with one byte replaced, at <paramref name="offset"/> from the end.</summary>
    public static byte[] Flip(byte[] stored, int offsetFromEnd)
    {
        var copy = (byte[])stored.Clone();
        copy[copy.Length - 1 - offsetFromEnd] ^= 0xFF;
        return copy;
    }

    private static byte[] Deflate(ReadOnlySpan<byte> content)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(content);
        }

        return output.ToArray();
    }

    /// <summary>A JSON-like content of exactly <paramref name="bytes"/> bytes, unique per <paramref name="marker"/>.</summary>
    public static byte[] Content(int bytes, string marker = "content")
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{{\"marker\":\"{marker}\",\"items\":[");
        for (var index = 0; builder.Length < bytes; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{{\"id\":{index},\"v\":{index * 31}}},");
        }

        builder.Append("]}");
        var text = builder.ToString();
        return Encoding.UTF8.GetBytes(text.Length > bytes ? text[..bytes] : text);
    }
}
