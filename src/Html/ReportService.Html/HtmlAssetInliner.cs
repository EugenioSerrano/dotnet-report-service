using System.Net;
using System.Text.RegularExpressions;

namespace ReportService.Templates;

/// <summary>
/// Resolves one packaged, repository-relative reference against the correct base
/// (the package root for an HTML-element reference or a reference in an inline
/// <c>&lt;style&gt;</c>/<c>style=""</c>; the referencing stylesheet's own directory
/// for a CSS reference inside a packaged stylesheet — SPEC §6 B.2), checks
/// containment and inlines it (ADR-046 §3.3). Classification happens on the
/// percent-decoded value (so an encoded traversal or scheme cannot slip through),
/// always before any package read, and the inliner never throws on a bad reference
/// — it returns a coarse <see cref="HtmlRefusal"/>. It owns the closed-list CSS
/// <c>@import</c>/<c>url()</c>/<c>@font-face</c> scan, the import cycle detection and
/// the depth bound, and exposes CSS processing for arbitrary CSS text (inline
/// <c>&lt;style&gt;</c> and <c>style=""</c>), reused by the assembler.
/// </summary>
public sealed partial class HtmlAssetInliner
{
    /// <summary>The default bound on nested <c>@import</c> depth (SPEC §14 D5).</summary>
    public const int DefaultMaxImportDepth = 4;

    /// <summary>An inliner with the default <c>@import</c> depth bound.</summary>
    public HtmlAssetInliner()
        : this(DefaultMaxImportDepth)
    {
    }

    /// <summary>An inliner with a given <c>@import</c> depth bound.</summary>
    /// <param name="maxImportDepth">The maximum number of nested <c>@import</c> hops below the entry stylesheet.</param>
    public HtmlAssetInliner(int maxImportDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxImportDepth, 1);
        MaxImportDepth = maxImportDepth;
    }

    /// <summary>The bound on nested <c>@import</c> depth this inliner enforces.</summary>
    public int MaxImportDepth { get; }

    /// <summary>
    /// Inlines a relative <c>&lt;link rel="stylesheet"&gt;</c> reference: resolves it against
    /// the package root, reads the stylesheet, resolves and inlines its <c>@import</c>s and
    /// <c>url()</c>s, and returns the resulting CSS text (to be wrapped in a <c>&lt;style&gt;</c>).
    /// </summary>
    /// <param name="href">The stylesheet reference, as the template wrote it.</param>
    /// <param name="package">The taken package's files.</param>
    public HtmlInlineOutcome InlineStylesheetLink(string href, ITemplatePackageFiles package)
    {
        ArgumentNullException.ThrowIfNull(package);
        const string region = "stylesheet";

        var (cause, key) = ResolveReference(string.Empty, href);
        if (cause is { } refuseCause)
        {
            return HtmlInlineOutcome.Refuse(new HtmlRefusal(refuseCause, href, region));
        }

        if (!SafeExists(package, key))
        {
            return HtmlInlineOutcome.Refuse(new HtmlRefusal(HtmlRefusalCause.NonInlinableOrOffList, href, region));
        }

        var stack = new HashSet<string>(StringComparer.Ordinal) { key };
        var css = ProcessStylesheet(key, package, stack, depth: 0, out var refusal);
        return refusal is not null ? HtmlInlineOutcome.Refuse(refusal) : HtmlInlineOutcome.Ok(css!);
    }

    /// <summary>
    /// Inlines a relative HTML-element asset reference (<c>&lt;img src&gt;</c>, a font or image)
    /// as a <c>data:</c> URI, resolved against the package root.
    /// </summary>
    /// <param name="src">The asset reference, as the template wrote it.</param>
    /// <param name="package">The taken package's files.</param>
    public HtmlInlineOutcome InlineElementAsset(string src, ITemplatePackageFiles package)
    {
        ArgumentNullException.ThrowIfNull(package);
        const string region = "element";

        var (cause, key) = ResolveReference(string.Empty, src);
        if (cause is { } refuseCause)
        {
            return HtmlInlineOutcome.Refuse(new HtmlRefusal(refuseCause, src, region));
        }

        if (!SafeExists(package, key))
        {
            return HtmlInlineOutcome.Refuse(new HtmlRefusal(HtmlRefusalCause.NonInlinableOrOffList, src, region));
        }

        var dataUri = SafeDataUri(key, package, out var refusal);
        return refusal is not null ? HtmlInlineOutcome.Refuse(refusal) : HtmlInlineOutcome.Ok(dataUri!);
    }

    /// <summary>
    /// Resolves and inlines the <c>@import</c>s and <c>url()</c>s of an arbitrary CSS fragment —
    /// the content of an inline <c>&lt;style&gt;</c> block or a <c>style=""</c> attribute — against
    /// <paramref name="baseDir"/> (the package root for both), returning the inlined CSS or a
    /// refusal. Never throws.
    /// </summary>
    /// <param name="cssText">The CSS fragment.</param>
    /// <param name="baseDir">The base directory the references resolve against (empty = package root).</param>
    /// <param name="package">The taken package's files.</param>
    /// <param name="refusal">The refusal reason when a reference cannot be inlined; otherwise <see langword="null"/>.</param>
    internal string? ProcessCss(string cssText, string baseDir, ITemplatePackageFiles package, out HtmlRefusal? refusal) =>
        ProcessCss(cssText, baseDir, new HashSet<string>(StringComparer.Ordinal), depth: 0, package, out refusal);

    // ---- CSS processing ----

    private string? ProcessStylesheet(
        string key,
        ITemplatePackageFiles package,
        HashSet<string> importStack,
        int depth,
        out HtmlRefusal? refusal)
    {
        var text = SafeReadText(package, key, out refusal);
        if (refusal is not null)
        {
            return null;
        }

        return ProcessCss(text!, DirectoryOf(key), importStack, depth, package, out refusal);
    }

    private string? ProcessCss(
        string text,
        string dir,
        HashSet<string> importStack,
        int depth,
        ITemplatePackageFiles package,
        out HtmlRefusal? refusal)
    {
        refusal = null;
        HtmlRefusal? problem = null;

        // 1. Resolve @imports first, into placeholders, so the url() pass below does not
        //    touch an @import's own url(...) nor the already-inlined data: URIs it brings in.
        var imports = new List<string>();
        var withoutImports = ImportPattern().Replace(text, match =>
        {
            if (problem is not null)
            {
                return match.Value;
            }

            var reference = match.Groups["u"].Success ? match.Groups["u"].Value : match.Groups["q"].Value;
            var inner = ResolveImport(reference, dir, package, importStack, depth, out problem);
            if (problem is not null)
            {
                return match.Value;
            }

            imports.Add(inner!);
            return "\0IMPORT" + (imports.Count - 1) + "\0";
        });

        if (problem is not null)
        {
            refusal = problem;
            return null;
        }

        // 2. Resolve the fragment's own url(...) references against its directory.
        var inlined = UrlPattern().Replace(withoutImports, match =>
        {
            if (problem is not null)
            {
                return match.Value;
            }

            var reference = match.Groups["u"].Value;
            if (reference.StartsWith("data:", StringComparison.Ordinal) || reference.StartsWith("\0IMPORT", StringComparison.Ordinal))
            {
                return match.Value;
            }

            var resolved = ResolveUrl(reference, dir, package, out problem);
            return problem is not null ? match.Value : "url(" + resolved + ")";
        });

        if (problem is not null)
        {
            refusal = problem;
            return null;
        }

        // 3. Splice the resolved @import bodies back in.
        for (var i = 0; i < imports.Count; i++)
        {
            inlined = inlined.Replace("\0IMPORT" + i + "\0", imports[i], StringComparison.Ordinal);
        }

        return inlined;
    }

    private string? ResolveImport(
        string reference,
        string dir,
        ITemplatePackageFiles package,
        HashSet<string> importStack,
        int depth,
        out HtmlRefusal? refusal)
    {
        const string region = "import";

        var (cause, key) = ResolveReference(dir, reference);
        if (cause is { } refuseCause)
        {
            refusal = new HtmlRefusal(refuseCause, reference, region);
            return null;
        }

        if (!SafeExists(package, key))
        {
            refusal = new HtmlRefusal(HtmlRefusalCause.NonInlinableOrOffList, reference, region);
            return null;
        }

        if (importStack.Contains(key) || depth + 1 > MaxImportDepth)
        {
            refusal = new HtmlRefusal(HtmlRefusalCause.ImportCycleOrDepth, reference, region);
            return null;
        }

        importStack.Add(key);
        var inner = ProcessStylesheet(key, package, importStack, depth + 1, out refusal);
        importStack.Remove(key);
        return inner;
    }

    private static string? ResolveUrl(string reference, string dir, ITemplatePackageFiles package, out HtmlRefusal? refusal)
    {
        const string region = "url";

        var (cause, key) = ResolveReference(dir, reference);
        if (cause is { } refuseCause)
        {
            refusal = new HtmlRefusal(refuseCause, reference, region);
            return null;
        }

        if (!SafeExists(package, key))
        {
            refusal = new HtmlRefusal(HtmlRefusalCause.NonInlinableOrOffList, reference, region);
            return null;
        }

        return SafeDataUri(key, package, out refusal);
    }

    // ---- shared resolution, classification and encoding (reused by the assembler) ----

    /// <summary>
    /// Classifies a reference that reaches outside the package by its shape — a remote or
    /// protocol-relative URL, an absolute path, a drive-letter, a UNC or backslash path —
    /// on its <b>percent-decoded</b> value, returning <see cref="HtmlRefusalCause.OutsidePackage"/>,
    /// or <see langword="null"/> when the reference is relative and must be resolved.
    /// </summary>
    /// <param name="reference">The reference to classify.</param>
    internal static HtmlRefusalCause? ClassifyExternal(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return HtmlRefusalCause.NonInlinableOrOffList;
        }

        var raw = reference.Trim();
        if (IsExternalShape(raw))
        {
            return HtmlRefusalCause.OutsidePackage;
        }

        var decoded = Decode(raw);
        return IsExternalShape(decoded) ? HtmlRefusalCause.OutsidePackage : null;
    }

    private static bool IsExternalShape(string value)
    {
        var slashed = value.Replace('\\', '/');
        return slashed.Contains("://", StringComparison.Ordinal)
            || slashed.StartsWith("//", StringComparison.Ordinal)
            || slashed.StartsWith('/')
            || (slashed.Length >= 2 && slashed[1] == ':');
    }

    /// <summary>
    /// Resolves a relative reference against a base directory, classifying it on its
    /// percent-decoded value and normalising it before the containment check. Returns the
    /// coarse refusal cause when the reference reaches outside the package
    /// (<see cref="HtmlRefusalCause.OutsidePackage"/>) or escapes the root
    /// (<see cref="HtmlRefusalCause.TraversalEscape"/>), or the resolved package-relative key.
    /// Never reads the package and never throws.
    /// </summary>
    /// <param name="baseDir">The base directory (empty for the package root).</param>
    /// <param name="reference">The reference.</param>
    internal static (HtmlRefusalCause? Cause, string Key) ResolveReference(string baseDir, string reference)
    {
        var external = ClassifyExternal(reference);
        if (external is { } cause)
        {
            return (cause, string.Empty);
        }

        var decoded = Decode(reference.Trim()).Replace('\\', '/');

        var parts = new List<string>();
        foreach (var segment in baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            parts.Add(segment);
        }

        foreach (var segment in decoded.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    return (HtmlRefusalCause.TraversalEscape, string.Empty);
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        var key = string.Join('/', parts);
        return key.Length == 0 ? (HtmlRefusalCause.TraversalEscape, string.Empty) : ((HtmlRefusalCause?)null, key);
    }

    /// <summary>Returns the first <c>@import</c> or <c>url()</c> reference in a CSS fragment, or <see langword="null"/>.</summary>
    /// <param name="css">The CSS fragment.</param>
    internal static string? FirstCssReference(string css)
    {
        var import = ImportPattern().Match(css);
        if (import.Success)
        {
            return import.Groups["u"].Success ? import.Groups["u"].Value : import.Groups["q"].Value;
        }

        foreach (Match url in UrlPattern().Matches(css))
        {
            var reference = url.Groups["u"].Value;
            if (!reference.StartsWith("data:", StringComparison.Ordinal))
            {
                return reference;
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes a reference value before classification and before any read: first the HTML
    /// character references a browser would resolve (named <c>&amp;period;</c>, decimal
    /// <c>&amp;#46;</c>, hex <c>&amp;#x2e;</c>), then percent-encoding — so an entity- or
    /// percent-encoded traversal or scheme cannot slip past containment, and an
    /// entity-encoded packaged reference still resolves to its real packaged name.
    /// </summary>
    /// <summary>
    /// Decodes an attribute value (HTML entities then percent-encoding) before the floor detects
    /// a security-relevant attribute (<c>style=</c>, <c>http-equiv</c>, <c>content</c>, <c>rel</c>)
    /// or classifies a reference, so an encoded form is handled exactly as its decoded form
    /// (ADR-047 §3.3 item 5(b)).
    /// </summary>
    /// <param name="value">The raw attribute value.</param>
    internal static string DecodeValue(string value) => Decode(value);

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(WebUtility.HtmlDecode(value));
        }
        catch (UriFormatException)
        {
            return WebUtility.HtmlDecode(value);
        }
    }

    private static bool SafeExists(ITemplatePackageFiles package, string key)
    {
        try
        {
            return package.Exists(key);
        }
        catch (TemplatePackageException)
        {
            return false;
        }
    }

    private static string? SafeReadText(ITemplatePackageFiles package, string key, out HtmlRefusal? refusal)
    {
        try
        {
            refusal = null;
            return package.ReadAllText(key);
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException)
        {
            refusal = new HtmlRefusal(HtmlRefusalCause.NonInlinableOrOffList, key, "read");
            return null;
        }
    }

    private static string? SafeDataUri(string key, ITemplatePackageFiles package, out HtmlRefusal? refusal)
    {
        try
        {
            refusal = null;
            using var stream = package.OpenRead(key);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return "data:" + MimeType(key) + ";base64," + Convert.ToBase64String(buffer.ToArray());
        }
        catch (Exception exception) when (exception is TemplatePackageException or IOException)
        {
            refusal = new HtmlRefusal(HtmlRefusalCause.NonInlinableOrOffList, key, "read");
            return null;
        }
    }

    private static string DirectoryOf(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? string.Empty : key[..slash];
    }

    /// <summary>Maps a packaged file's extension to a media type for its <c>data:</c> URI.</summary>
    /// <param name="key">The resolved package-relative key.</param>
    internal static string MimeType(string key)
    {
        var dot = key.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : key[(dot + 1)..].ToLowerInvariant();
        return extension switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "svg" => "image/svg+xml",
            "webp" => "image/webp",
            "bmp" => "image/bmp",
            "ico" => "image/x-icon",
            "woff2" => "font/woff2",
            "woff" => "font/woff",
            "ttf" => "font/ttf",
            "otf" => "font/otf",
            "eot" => "application/vnd.ms-fontobject",
            "css" => "text/css",
            _ => "application/octet-stream",
        };
    }

    [GeneratedRegex(
        """@import\s+(?:url\(\s*['"]?(?<u>[^'")\s]+)['"]?\s*\)|['"](?<q>[^'"]+)['"])\s*;?""",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImportPattern();

    [GeneratedRegex("""url\(\s*['"]?(?<u>[^'")\s]+)['"]?\s*\)""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
