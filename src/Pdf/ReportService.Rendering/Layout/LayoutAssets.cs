using System.Security.Cryptography;
using System.Text;

namespace ReportService.Rendering.Layout;

/// <summary>
/// The three inline scripts the accounting print serves, in the order it serves them,
/// and the content-security hash sources that allow exactly those three: the
/// auto-start configuration that keeps Paged.js from starting before the split, the
/// vendored Paged.js 0.4.3 polyfill (unchanged, pinned by SHA-256) and the product's
/// own layout script. They are embedded resources of this assembly, so the published
/// worker image carries them and no job downloads anything. The pins are asserted by
/// the tests; a changed asset cannot ship silently.
/// </summary>
public static class LayoutAssets
{
    /// <summary>The SHA-256 of the vendored Paged.js 0.4.3 polyfill, as measured and as the ADR records it.</summary>
    public const string PagedPolyfillSha256 = "f59f361802416c770d549a647958649af2cf6601999924bc00e4f507dad5269f";

    /// <summary>The auto-start configuration: Paged.js waits for the split step's start.</summary>
    public const string ConfigurationScript = "window.PagedConfig = { auto: false };";

    private const string PagedPolyfillResource = "ReportService.Rendering.Layout.paged.polyfill.js";
    private const string ProductScriptResource = "ReportService.Rendering.Layout.page-accounting.js";

    private static readonly Lazy<string> LazyProduct = new(() => Read(ProductScriptResource));

    /// <summary>The vendored Paged.js 0.4.3 polyfill exactly as it ships.</summary>
    public static string PagedPolyfill { get; } = Read(PagedPolyfillResource);

    /// <summary>The product's own layout script.</summary>
    public static string ProductScript => LazyProduct.Value;

    /// <summary>The content-security hash source of the auto-start configuration.</summary>
    public static string ConfigurationHashSource { get; } = HashSource(ConfigurationScript);

    /// <summary>The content-security hash source of the vendored polyfill.</summary>
    public static string PagedPolyfillHashSource { get; } = HashSource(PagedPolyfill);

    /// <summary>The content-security hash source of the product layout script.</summary>
    public static string ProductHashSource { get; } = HashSource(ProductScript);

    /// <summary>The three script hash sources, in the order the scripts are served.</summary>
    public static IReadOnlyList<string> ScriptHashSources { get; } =
        [ConfigurationHashSource, PagedPolyfillHashSource, ProductHashSource];

    /// <summary>
    /// The document as the accounting print serves it: the composed block with its page
    /// rule, then the three inline scripts in the order the split needs them. They are
    /// appended after the markup, so they execute when it is parsed.
    /// </summary>
    /// <param name="document">The composed block followed by the page rule.</param>
    public static string AppendTo(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document
            + "<script>" + ConfigurationScript + "</script>"
            + "<script>" + PagedPolyfill + "</script>"
            + "<script>" + ProductScript + "</script>";
    }

    /// <summary>The SHA-256 of the served text as the browser reads it: UTF-8 without a byte-order mark.</summary>
    /// <param name="script">The script's text.</param>
    public static string Sha256Of(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(script)));
    }

    private static string HashSource(string script) =>
        "'sha256-" + Convert.ToBase64String(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(script))) + "'";

    private static string Read(string name)
    {
        using var stream = typeof(LayoutAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("the layout asset " + name + " is not embedded");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var span = bytes.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(span);
    }
}
