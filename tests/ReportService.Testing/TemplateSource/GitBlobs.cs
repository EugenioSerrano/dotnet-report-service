using System.Security.Cryptography;
using System.Text;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The git object names a template source is described with: a blob's is the
/// SHA-1 over <c>blob &lt;size&gt;\0</c> and the bytes, a tree's over the entries it
/// holds. The stub and the in-memory source compute them the same way, so a
/// test can hand a reader a hash that really is the hash of what it serves.
/// </summary>
public static class GitBlobs
{
    /// <summary>The git object name of <paramref name="content"/>.</summary>
    public static string BlobSha(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"blob {content.Length}\0"));
        var buffer = new byte[header.Length + content.Length];
        header.CopyTo(buffer, 0);
        content.CopyTo(buffer, header.Length);
        return Sha1Hex(buffer);
    }

    /// <summary>The git object name of a tree holding <paramref name="entries"/>, each <c>(mode, name, sha)</c>, already in git's order.</summary>
    public static string TreeSha(IEnumerable<(string Mode, string Name, string Sha)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var body = new MemoryStream();
        foreach (var entry in entries)
        {
            var header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"{entry.Mode} {entry.Name}\0"));
            body.Write(header, 0, header.Length);
            body.Write(Convert.FromHexString(entry.Sha), 0, 20);
        }

        var prefix = Encoding.ASCII.GetBytes(FormattableString.Invariant($"tree {body.Length}\0"));
        var buffer = new byte[prefix.Length + body.Length];
        prefix.CopyTo(buffer, 0);
        body.ToArray().CopyTo(buffer, prefix.Length);
        return Sha1Hex(buffer);
    }

    /// <summary>The lower-case hexadecimal SHA-1 of <paramref name="bytes"/>.</summary>
    public static string Sha1Hex(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
}
