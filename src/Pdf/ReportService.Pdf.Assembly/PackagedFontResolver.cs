using PdfSharp.Fonts;

namespace ReportService.Pdf.Assembly;

/// <summary>
/// The one font resolver of the writing library: it serves the packaged font
/// bytes the numbering contract names and nothing else. A face is registered under
/// a name derived from the font's SHA-256, so two assemblies of the same package
/// share one face and no face is ever resolved from the host's font folders.
/// </summary>
public sealed class PackagedFontResolver : IFontResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _faces = new(StringComparer.Ordinal);

    /// <summary>The process's single resolver.</summary>
    public static PackagedFontResolver Instance { get; } = new();

    /// <summary>Registers a font's bytes under the face name derived from its hash.</summary>
    /// <param name="font">The packaged font.</param>
    /// <returns>The face name the writing library resolves.</returns>
    public string Register(NumberingFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var face = "packaged-" + font.Sha256[..24];
        lock (_gate)
        {
            _faces[face] = font.Bytes;
        }

        return face;
    }

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        lock (_gate)
        {
            return _faces.ContainsKey(familyName) ? new FontResolverInfo(familyName) : null;
        }
    }

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        lock (_gate)
        {
            return _faces.TryGetValue(faceName, out var bytes) ? bytes : null;
        }
    }
}
