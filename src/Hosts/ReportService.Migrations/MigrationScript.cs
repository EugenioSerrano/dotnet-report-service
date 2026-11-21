using System.Security.Cryptography;
using System.Text;

namespace ReportService.Migrations;

/// <summary>
/// One migration script as it ships: its file name, its SQL text and the
/// lower-case hexadecimal SHA-256 of the bytes that are embedded. The checksum
/// is what the journal records, so an applied script that changes afterwards is
/// refused instead of applied twice.
/// </summary>
public sealed record MigrationScript(string Name, string Sql, string Checksum)
{
    /// <summary>The lower-case hexadecimal SHA-256 of <paramref name="bytes"/>.</summary>
    public static string ChecksumOf(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The SQL text of <paramref name="bytes"/>, decoded as UTF-8.</summary>
    public static string SqlOf(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
