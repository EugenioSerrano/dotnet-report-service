using PdfSharp.Fonts;

namespace TwoPassSpike;

/// <summary>
/// Serves the Liberation Sans regular face — the content font of the first pass,
/// a file of the PDF base image — to the PDF library. The library finds no system
/// fonts on Linux, so the font comes from here. Any other family or face is an
/// error: nothing is substituted silently. Adapted from spikes/03-pdf-assembly
/// (DejaVuFontResolver.cs).
/// </summary>
internal sealed class LiberationFontResolver(string fontFile) : IFontResolver
{
    public const string FamilyName = "Liberation Sans";
    public const string FaceName = "LiberationSans-Regular";
    public const string DefaultFile = "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";

    private readonly byte[] bytes = File.ReadAllBytes(fontFile);

    public string FontFile { get; } = fontFile;

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        if (!string.Equals(familyName, FamilyName, StringComparison.OrdinalIgnoreCase) || isBold || isItalic)
        {
            throw new InvalidOperationException($"Font '{familyName}' bold={isBold} italic={isItalic} is not available; only the regular face of '{FamilyName}' is served.");
        }

        return new FontResolverInfo(FaceName);
    }

    public byte[]? GetFont(string faceName) =>
        faceName == FaceName ? bytes : throw new InvalidOperationException($"Face '{faceName}' is not served.");

    /// <summary>Installs the resolver once per process, before any font is created.</summary>
    public static void Install(string fontFile)
    {
        if (!File.Exists(fontFile))
        {
            throw new FileNotFoundException($"Font file {fontFile} not found.", fontFile);
        }

        GlobalFontSettings.FontResolver ??= new LiberationFontResolver(fontFile);
    }
}
