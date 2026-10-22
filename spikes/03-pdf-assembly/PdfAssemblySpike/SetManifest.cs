using System.Security.Cryptography;
using System.Text.Json;

namespace PdfAssemblySpike;

/// <summary>
/// One partial PDF of a set, in the order it must appear in the output. Sha256 is
/// the hash of the raw file; MaskedSha256 the hash of its bytes with the two
/// random fields the PDF library writes masked in memory.
/// </summary>
internal sealed record PartEntry(int Order, string File, int Pages, int FirstGlobalPage, string Sha256, string? MaskedSha256 = null);

/// <summary>
/// Describes one set of partial PDFs: the seed, the total page count and every
/// part in output order. Assembly and verification read files only through it.
/// </summary>
internal sealed record SetManifest(ulong Seed, int TargetPages, IReadOnlyList<PartEntry> Parts)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static SetManifest Load(string path) =>
        JsonSerializer.Deserialize<SetManifest>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Empty manifest: {path}");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");

    public static string PartPath(string manifestPath, PartEntry part) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, part.File);

    /// <summary>(part order, page within the part) for every global page; index 0 is page 1.</summary>
    public (int Part, int Page)[] PageMap()
    {
        var map = new (int Part, int Page)[TargetPages];
        foreach (var part in Parts)
        {
            for (int k = 1; k <= part.Pages; k++)
            {
                map[part.FirstGlobalPage + k - 2] = (part.Order, k);
            }
        }

        return map;
    }

    public PartEntry Part(int order) => Parts[order - 1].Order == order
        ? Parts[order - 1]
        : Parts.Single(p => p.Order == order);

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
