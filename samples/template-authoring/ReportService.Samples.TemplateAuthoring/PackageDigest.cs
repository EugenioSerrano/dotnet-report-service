using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// The local provenance digest of one package folder: the SHA-256 over every file
/// below it, in ordinal relative-path order, each fed as the UTF-8 path, a
/// <c>0x00</c> separator, the file's length as a big-endian 64-bit integer and the
/// file's bytes. It exists only because the candidate contract requires a
/// provenance value and a local folder has no publication descriptor; it is never
/// presented as a publication hash.
/// </summary>
public static class PackageDigest
{
    /// <summary>The lower-case hexadecimal SHA-256 of the package at <paramref name="packageDirectory"/>.</summary>
    /// <param name="packageDirectory">The package folder to digest.</param>
    public static string Of(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var root = Path.GetFullPath(packageDirectory);
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Full: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[8];
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Relative));
            hash.AppendData([0x00]);
            var bytes = File.ReadAllBytes(file.Full);
            BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
