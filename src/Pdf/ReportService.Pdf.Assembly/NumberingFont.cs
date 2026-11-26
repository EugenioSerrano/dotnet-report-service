using System.Security.Cryptography;

namespace ReportService.Pdf.Assembly;

/// <summary>
/// The font the global page number is drawn with: the family the template
/// declares, the package-relative file it is stored at, its size in points and
/// the bytes the take already validated. The resolver serves exactly these bytes,
/// so a stamp is never drawn with a font the host happens to have.
/// </summary>
/// <param name="Family">The family name the template declares.</param>
/// <param name="PackagePath">The package-relative path of the font file.</param>
/// <param name="SizePt">The size the stamp is drawn at, in points.</param>
/// <param name="Bytes">The font file's bytes.</param>
public sealed record NumberingFont(string Family, string PackagePath, double SizePt, byte[] Bytes)
{
    /// <summary>The SHA-256 of the font file, lower-case hexadecimal.</summary>
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Bytes));
}
