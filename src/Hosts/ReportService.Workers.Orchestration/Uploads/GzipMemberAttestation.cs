using System.Buffers.Binary;

namespace ReportService.Workers.Orchestration.Uploads;

/// <summary>
/// Whether a stored compressed object is one complete unit of compressed
/// content, decided from the object's own last eight bytes. A gzip object ends
/// with a trailer: the CRC-32 of the content it holds, then that content's
/// length modulo 2^32, both little-endian. The trailer is the last eight bytes
/// whatever the header carries — optional header fields extend the header, never
/// the trailer — so nothing is parsed and no position is guessed.
///
/// The object is one complete unit when it is long enough to hold a header, a
/// compressed stream and a trailer — twenty bytes — and its trailer agrees with
/// what was computed over the content that was read: the CRC-32 the service
/// computed and the length it counted. A compressed object whose content is
/// intact but whose own trailer is missing or cut therefore does not agree, and
/// a shorter object cannot end with a trailer at all.
///
/// This is the single place that decides; the storage port carries no such
/// concern and no other type repeats the comparison.
/// </summary>
public static class GzipMemberAttestation
{
    /// <summary>The bytes of the trailer at the end of a compressed object.</summary>
    public const int TrailerBytes = 8;

    /// <summary>
    /// The shortest object that can hold a header, a compressed stream and a
    /// trailer: ten, at least two and eight bytes.
    /// </summary>
    public const int MinimumObjectBytes = 20;

    /// <summary>
    /// True when the object ends with a trailer that agrees with the content
    /// read: its CRC-32 and its length, the length compared modulo 2^32 on both
    /// sides so a content longer than 4 GiB still agrees.
    /// </summary>
    /// <param name="tail">The object's last eight bytes, in the order they were read.</param>
    /// <param name="storedLength">The number of bytes the object holds.</param>
    /// <param name="contentCrc32">The CRC-32 computed over the content that was read.</param>
    /// <param name="contentLength">The number of content bytes that were read.</param>
    public static bool IsAttested(
        ReadOnlySpan<byte> tail,
        long storedLength,
        uint contentCrc32,
        long contentLength)
    {
        if (storedLength < MinimumObjectBytes || tail.Length < TrailerBytes)
        {
            return false;
        }

        var claimedCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(tail[..sizeof(uint)]);
        var claimedLength = BinaryPrimitives.ReadUInt32LittleEndian(tail[sizeof(uint)..]);

        return claimedCrc32 == contentCrc32 && claimedLength == unchecked((uint)contentLength);
    }
}
