using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace PdfAssemblySpike;

/// <summary>
/// Serves the DejaVu Sans font files to the PDF library. The library's Core build
/// finds no system fonts on Linux, so every font must come from here. Any other
/// family is an error: nothing is substituted silently.
/// </summary>
internal sealed class DejaVuFontResolver(string fontDirectory) : IFontResolver
{
    public const string FamilyName = "DejaVu Sans";
    public const string DefaultDirectory = "/usr/share/fonts/truetype/dejavu";

    private readonly ConcurrentDictionary<string, byte[]> _fonts = new(StringComparer.Ordinal);

    public string FontDirectory { get; } = fontDirectory;

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        if (!string.Equals(familyName, FamilyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Font family '{familyName}' is not available; only '{FamilyName}' is served.");
        }

        string face = isBold ? "DejaVuSans-Bold" : "DejaVuSans";
        return new FontResolverInfo(face, false, isItalic);
    }

    public byte[]? GetFont(string faceName) =>
        _fonts.GetOrAdd(faceName, face => File.ReadAllBytes(Path.Combine(FontDirectory, face + ".ttf")));

    /// <summary>Installs the resolver once per process, before any font is created.</summary>
    public static void Install(string? fontDirectory)
    {
        if (GlobalFontSettings.FontResolver is null)
        {
            GlobalFontSettings.FontResolver = new DejaVuFontResolver(fontDirectory ?? DefaultDirectory);
        }
    }
}
