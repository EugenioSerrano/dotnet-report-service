using System.Text;
using System.Text.RegularExpressions;

namespace ReportService.Templates;

/// <summary>
/// Assembles one self-contained <c>.html</c> from the composed document-mode
/// fragment sequence by a bounded sequential streaming write (ADR-046 §3.1–§3.6):
/// the <c>documentStart</c> once, each row/group fragment in order, the
/// <c>documentEnd</c> once. As the non-repeated regions stream past it resolves and
/// inlines the template's packaged, repository-relative asset references — the closed
/// list of five kinds, wherever the CSS sits: a packaged stylesheet, an inline
/// <c>&lt;style&gt;</c> block, or a <c>style=""</c> attribute (SPEC §6 B.1) — through
/// <see cref="HtmlAssetInliner"/>. Every other resource-loading reference, a reference
/// reaching outside the package, a traversal escape, a repeated-region reference or an
/// import cycle/over-depth is refused, and no complete file is written.
/// <para>
/// <b>Refused ⇒ discard (hand-off contract to BOLT-002).</b> The write is bounded and
/// streaming — the whole document is never buffered — so on a refusal the destination
/// may already hold the bytes of earlier non-repeated fragments (never the offending
/// one, never a <c>&lt;/html&gt;</c>, never an asset that could not be contained). A
/// <see cref="HtmlAssemblyResult"/> whose <see cref="HtmlAssemblyResult.IsProduced"/> is
/// <see langword="false"/> means the caller MUST discard the destination stream and
/// publish nothing (ADR-046 §3.5 item 1); only a produced result is a complete,
/// self-contained document.
/// </para>
/// </summary>
public sealed partial class HtmlOutputAssembler
{
    // The record ordinal of the repeated fragment being read, for an image refusal's diagnostic.
    private long repeatedOrdinal;

    /// <summary>The default bound on nested <c>@import</c> depth (SPEC §14 D5).</summary>
    public const int DefaultMaxImportDepth = HtmlAssetInliner.DefaultMaxImportDepth;

    private const string StyleClose = "</style>";
    private const string CommentOpen = "<!--";
    private const string CommentClose = "-->";
    private const string DoctypeOpen = "<!doctype";

    /// <summary>
    /// The fixed Content-Security-Policy <c>&lt;meta&gt;</c> the assembler emits as the first child
    /// of <c>&lt;head&gt;</c> of every produced <c>.html</c> (ADR-047 §3.8). The directive string is
    /// fixed and never weakened: <c>default-src 'none'</c> fetch-blocks any residual the floor
    /// missed in a CSP-honouring browser; the allowances are exactly what the inlined content needs.
    /// </summary>
    private const string CspMeta =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:\">";

    /// <summary>The charset meta the assembler emits when it establishes a <c>&lt;head&gt;</c>.</summary>
    private const string CharsetMeta = "<meta charset=\"utf-8\">";

    /// <summary>The bound on the <c>documentStart</c> head scan, counted in UTF-8 bytes (ADR-047 §3.8 item 3).</summary>
    private const int HeadScanLimitBytes = 64 * 1024;

    /// <summary>
    /// The concrete "head-internal element" set (SPEC §6 C.1 gives it as "e.g."): elements that may
    /// sit in <c>&lt;head&gt;</c> and do NOT count as a rendered body element for the body-before-head
    /// test. A closing tag has an empty element name and never counts; <c>&lt;!DOCTYPE&gt;</c>,
    /// comments and whitespace are skipped by the scan before classification.
    /// </summary>
    private static readonly HashSet<string> HeadInternalElements = new(StringComparer.Ordinal)
    {
        "meta", "title", "base", "link", "style", "script", "noscript", "template",
    };

    private readonly HtmlAssetInliner inliner;

    /// <summary>An assembler with the default <c>@import</c> depth bound.</summary>
    public HtmlOutputAssembler()
        : this(DefaultMaxImportDepth)
    {
    }

    /// <summary>An assembler with a given <c>@import</c> depth bound.</summary>
    /// <param name="maxImportDepth">The maximum number of nested <c>@import</c> hops below the entry stylesheet.</param>
    public HtmlOutputAssembler(int maxImportDepth) => inliner = new HtmlAssetInliner(maxImportDepth);

    /// <summary>
    /// Assembles <paramref name="block"/> over <paramref name="package"/> into
    /// <paramref name="destination"/>, returning whether one self-contained <c>.html</c> was
    /// produced or the assembly was refused.
    /// <para>
    /// <b>Refused ⇒ discard.</b> When the returned result is not produced
    /// (<see cref="HtmlAssemblyResult.IsProduced"/> is <see langword="false"/>), the write was
    /// bounded and may have left the bytes of earlier fragments in
    /// <paramref name="destination"/>; the caller MUST discard that stream and publish nothing.
    /// Only a produced result is a complete self-contained document.
    /// </para>
    /// </summary>
    /// <param name="block">The composed document-mode block and its fragment map.</param>
    /// <param name="package">The taken package's files — the asset set to inline from.</param>
    /// <param name="destination">The stream the output is written to.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    public async Task<HtmlAssemblyResult> AssembleAsync(
        ComposedBlock block,
        ITemplatePackageFiles package,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(destination);

        foreach (var fragment in block.Fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var region = RegionName(fragment.Kind);

            if (IsRepeatedRegion(fragment.Kind))
            {
                // A repeated row/group may carry no resource-loading reference (ADR-046 §3.3 item 2);
                // its original bytes are then streamed through unchanged (byte-fidelity).
                // The record the fragment prints names a refused image in the diagnostic (ADR-058).
                repeatedOrdinal = fragment.RecordOrdinal;
                ProcessFragment(block.TextOf(fragment), repeated: true, package, region, out var repeatedRefusal);
                if (repeatedRefusal is not null)
                {
                    return HtmlAssemblyResult.Refused(repeatedRefusal);
                }

                await destination.WriteAsync(block.Html.AsMemory(fragment.Start, fragment.Length), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var text = block.TextOf(fragment);
            if (fragment.Kind == FragmentKind.DocumentStart)
            {
                // Emit the Content-Security-Policy <meta> as the first child of <head>, establishing
                // a conforming head when documentStart has none, or refusing when it cannot (ADR-047
                // §3.8). Runs before inlining so the CSP meta and any established head are streamed
                // ahead of all content; a refusal here writes nothing (documentStart is the first
                // fragment).
                text = ApplyHeadConformance(text, out var headRefusal);
                if (headRefusal is not null)
                {
                    return HtmlAssemblyResult.Refused(headRefusal);
                }
            }

            var transformed = ProcessFragment(text!, repeated: false, package, region, out var refusal);
            if (refusal is not null)
            {
                return HtmlAssemblyResult.Refused(refusal);
            }

            await destination.WriteAsync(Encoding.UTF8.GetBytes(transformed!), cancellationToken).ConfigureAwait(false);
        }

        return HtmlAssemblyResult.Produced();
    }

    /// <summary>
    /// Emits the CSP <c>&lt;meta&gt;</c> as the first child of <c>&lt;head&gt;</c> of the
    /// <c>documentStart</c> fragment, establishing a conforming head when it has none, or refusing
    /// with <see cref="HtmlRefusalCause.HeadNotConformable"/> when it cannot (ADR-047 §3.8 item 3).
    /// A single deterministic bounded forward scan (≤ 64 KiB bytes, ASCII case-insensitive, skipping
    /// a leading <c>&lt;!DOCTYPE&gt;</c>, comments and whitespace) picks exactly one outcome:
    /// <list type="bullet">
    /// <item>one <c>&lt;head&gt;</c>, no rendered body element before it → inject the CSP meta right
    /// after the <c>&lt;head …&gt;</c> open tag;</item>
    /// <item>no <c>&lt;head&gt;</c>, an <c>&lt;html&gt;</c> present → establish a head right after the
    /// <c>&lt;html …&gt;</c> open tag (CSP meta first + a charset);</item>
    /// <item>no <c>&lt;head&gt;</c> and no <c>&lt;html&gt;</c>, but rendered content present →
    /// establish a head at the content start;</item>
    /// <item>a rendered body element before an existing <c>&lt;head&gt;</c>, more than one
    /// <c>&lt;head&gt;</c>, or no anchor within the bounded scan → refuse.</item>
    /// </list>
    /// </summary>
    private static string? ApplyHeadConformance(string documentStart, out HtmlRefusal? refusal)
    {
        refusal = null;
        var html = documentStart;
        var n = html.Length;
        var i = 0;
        var scannedBytes = 0;

        var htmlOpenEnd = -1;  // index just past the '>' of the first <html …>
        var headOpenPos = -1;  // index of the '<' of the first <head
        var headOpenEnd = -1;  // index just past the '>' of the first <head …>
        var headCount = 0;
        var firstRenderedPos = -1;
        var contentStart = -1; // first non-DOCTYPE/comment/whitespace position

        while (i < n && scannedBytes <= HeadScanLimitBytes)
        {
            var c = html[i];

            if (char.IsWhiteSpace(c))
            {
                scannedBytes++;
                i++;
                continue;
            }

            if (StartsWithIgnoreCase(html, i, CommentOpen))
            {
                var close = html.IndexOf(CommentClose, i + CommentOpen.Length, StringComparison.Ordinal);
                var end = close < 0 ? n : close + CommentClose.Length;
                scannedBytes += Encoding.UTF8.GetByteCount(html.AsSpan(i, end - i));
                i = end;
                continue;
            }

            if (StartsWithIgnoreCase(html, i, DoctypeOpen))
            {
                var close = html.IndexOf('>', i);
                var end = close < 0 ? n : close + 1;
                scannedBytes += Encoding.UTF8.GetByteCount(html.AsSpan(i, end - i));
                i = end;
                continue;
            }

            if (contentStart < 0)
            {
                contentStart = i;
            }

            if (c != '<')
            {
                // Rendered text content.
                if (firstRenderedPos < 0)
                {
                    firstRenderedPos = i;
                }

                scannedBytes += Encoding.UTF8.GetByteCount(html.AsSpan(i, 1));
                i++;
                continue;
            }

            var tagEnd = FindTagEnd(html, i);
            if (tagEnd < 0)
            {
                break;
            }

            var name = ElementNamePattern().Match(html[i..(tagEnd + 1)]).Groups["name"].Value.ToLowerInvariant();

            if (name == "html")
            {
                if (htmlOpenEnd < 0)
                {
                    htmlOpenEnd = tagEnd + 1;
                }
            }
            else if (name == "head")
            {
                if (headOpenPos < 0)
                {
                    headOpenPos = i;
                    headOpenEnd = tagEnd + 1;
                }

                headCount++;
            }
            else if (name.Length > 0 && !HeadInternalElements.Contains(name))
            {
                // A rendered body element (<body>, <table>, <div>, …). A closing tag yields an
                // empty element name and never counts.
                if (firstRenderedPos < 0)
                {
                    firstRenderedPos = i;
                }
            }

            scannedBytes += Encoding.UTF8.GetByteCount(html.AsSpan(i, tagEnd + 1 - i));
            i = tagEnd + 1;
        }

        if (headCount > 1)
        {
            refusal = HeadRefusal("duplicate <head>");
            return null;
        }

        if (headCount == 1)
        {
            if (firstRenderedPos >= 0 && firstRenderedPos < headOpenPos)
            {
                refusal = HeadRefusal("a rendered body element precedes <head>");
                return null;
            }

            return html[..headOpenEnd] + CspMeta + html[headOpenEnd..];
        }

        // No <head> anywhere in the scanned window.
        if (contentStart < 0)
        {
            refusal = HeadRefusal("no head/html/content anchor within the bounded head scan");
            return null;
        }

        var establishedHead = "<head>" + CspMeta + CharsetMeta + "</head>";
        return htmlOpenEnd >= 0
            ? html[..htmlOpenEnd] + establishedHead + html[htmlOpenEnd..]
            : html[..contentStart] + establishedHead + html[contentStart..];
    }

    private static HtmlRefusal HeadRefusal(string detail) =>
        new(HtmlRefusalCause.HeadNotConformable, detail, "documentStart");

    private static bool IsRepeatedRegion(FragmentKind kind) =>
        kind is FragmentKind.Row or FragmentKind.GroupStart or FragmentKind.GroupEnd;

    private static string RegionName(FragmentKind kind) => kind switch
    {
        FragmentKind.DocumentStart => "documentStart",
        FragmentKind.DocumentEnd => "documentEnd",
        FragmentKind.Row => "row",
        FragmentKind.GroupStart => "groupStart",
        FragmentKind.GroupEnd => "groupEnd",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Walks a fragment quote-aware (a <c>&gt;</c> inside a quoted attribute value never ends a
    /// tag), inlining the packaged references of its inline <c>&lt;style&gt;</c> blocks,
    /// <c>style=""</c> attributes and element assets when it is a non-repeated region, and
    /// refusing any resource-loading reference when it is a repeated region.
    /// </summary>
    private string? ProcessFragment(string html, bool repeated, ITemplatePackageFiles package, string region, out HtmlRefusal? refusal)
    {
        refusal = null;
        var builder = new StringBuilder(html.Length);
        var i = 0;
        var n = html.Length;

        while (i < n)
        {
            var c = html[i];
            if (c != '<')
            {
                builder.Append(c);
                i++;
                continue;
            }

            if (StartsWithIgnoreCase(html, i, CommentOpen))
            {
                // An HTML comment is inert: a resource-like reference inside it is not a
                // resource load (ADR-046 §3.3 item 4). Emit it verbatim — never scan,
                // inline or refuse its content — and resume after the close (or at end
                // when the comment is unterminated).
                var commentClose = html.IndexOf(CommentClose, i + CommentOpen.Length, StringComparison.Ordinal);
                var commentEnd = commentClose < 0 ? n : commentClose + CommentClose.Length;
                builder.Append(html, i, commentEnd - i);
                i = commentEnd;
                continue;
            }

            if (IsStyleBlockStart(html, i))
            {
                var openEnd = FindTagEnd(html, i);
                if (openEnd < 0)
                {
                    builder.Append(html, i, n - i);
                    break;
                }

                var openTag = html[i..(openEnd + 1)];
                var innerStart = openEnd + 1;
                var closeIndex = IndexOfIgnoreCase(html, StyleClose, innerStart);
                var innerEnd = closeIndex < 0 ? n : closeIndex;
                var inner = html[innerStart..innerEnd];

                if (repeated)
                {
                    var reference = HtmlAssetInliner.FirstCssReference(inner);
                    if (reference is not null)
                    {
                        refusal = new HtmlRefusal(HtmlRefusalCause.RepeatedRegion, reference, region);
                        return null;
                    }

                    builder.Append(openTag).Append(inner);
                }
                else
                {
                    var processed = inliner.ProcessCss(inner, string.Empty, package, out refusal);
                    if (refusal is not null)
                    {
                        return null;
                    }

                    builder.Append(openTag).Append(processed);
                }

                if (closeIndex < 0)
                {
                    i = n;
                }
                else
                {
                    builder.Append(StyleClose);
                    i = closeIndex + StyleClose.Length;
                }

                continue;
            }

            if (i + 1 < n && (char.IsLetter(html[i + 1]) || html[i + 1] == '/'))
            {
                var tagEnd = FindTagEnd(html, i);
                if (tagEnd < 0)
                {
                    builder.Append(html, i, n - i);
                    break;
                }

                var tag = html[i..(tagEnd + 1)];
                var replacement = ProcessTag(tag, repeated, package, region, out refusal);
                if (refusal is not null)
                {
                    return null;
                }

                builder.Append(replacement);
                i = tagEnd + 1;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private string? ProcessTag(string tag, bool repeated, ITemplatePackageFiles package, string region, out HtmlRefusal? refusal)
    {
        refusal = null;
        var name = ElementNamePattern().Match(tag).Groups["name"].Value.ToLowerInvariant();

        if (repeated)
        {
            // A record's image: an image data URI in an image source of a repeated part is
            // already inline, so it is passed through as it is, bounded at 1 MiB per value
            // (ADR-058 §3.3 item 2). Every other reference of the part is refused below.
            var admitted = AdmittedImageSources(name, tag, region, out refusal);
            if (refusal is not null)
            {
                return null;
            }

            // Region confinement: ANY resource-loading reference inside a repeated row/group is
            // refused, even a packaged, inlinable one, so no asset is inlined once per record
            // (ADR-047 §3.3 item 2).
            var style = Attribute(tag, "style");
            if (style is not null && HasCssResource(style))
            {
                refusal = new HtmlRefusal(
                    HtmlRefusalCause.RepeatedRegion,
                    HtmlAssetInliner.FirstCssReference(style) ?? style,
                    region);
                return null;
            }

            var repeatedReference = FirstResourceReference(name, tag, admitted);
            if (repeatedReference is not null)
            {
                refusal = new HtmlRefusal(HtmlRefusalCause.RepeatedRegion, repeatedReference, region);
                return null;
            }

            return tag;
        }

        // Non-repeated region: inline the closed list of five kinds, refuse every other
        // resource-loading reference — classified by its resource-loading ATTRIBUTE on any
        // element, not by the element name (ADR-047 §3.3 item 5(a)).
        var current = tag;
        var styleValue = Attribute(current, "style");
        if (styleValue is not null && HasCssResource(styleValue))
        {
            var processed = inliner.ProcessCss(styleValue, string.Empty, package, out refusal);
            if (refusal is not null)
            {
                return null;
            }

            current = ReplaceAttributeValue(current, "style", processed!);
        }

        // Closed inlinable kind 1: a relative <link rel="stylesheet"> -> an inlined <style>.
        if (name == "link")
        {
            var rel = Attribute(current, "rel");
            var href = Attribute(current, "href") ?? string.Empty;
            if (rel is not null && IsStylesheetRel(HtmlAssetInliner.DecodeValue(rel)))
            {
                var outcome = inliner.InlineStylesheetLink(href, package);
                if (!outcome.Inlined)
                {
                    refusal = outcome.Refusal;
                    return null;
                }

                return "<style>" + outcome.Content + StyleClose;
            }

            refusal = OffList(href, region);
            return null;
        }

        // Closed inlinable kind 4: a relative <img src> -> a data: URI. srcset is off the list.
        if (name == "img")
        {
            var srcset = Attribute(current, "srcset");
            if (srcset is not null)
            {
                refusal = OffList(srcset, region);
                return null;
            }

            var src = Attribute(current, "src");
            if (src is null)
            {
                return current;
            }

            var outcome = inliner.InlineElementAsset(src, package);
            if (!outcome.Inlined)
            {
                refusal = outcome.Refusal;
                return null;
            }

            return ReplaceAttributeValue(current, "src", outcome.Content!);
        }

        // Every other resource-loading reference (by attribute) is off the closed list -> refuse.
        var offListReference = FirstResourceReference(name, current);
        if (offListReference is not null)
        {
            refusal = OffList(offListReference, region);
            return null;
        }

        // An inline <script> (no src) is off the closed list too — the residual the CSP alone
        // does not block (ADR-047 §3.8; SPEC §6 B.1).
        if (name == "script")
        {
            refusal = new HtmlRefusal(HtmlRefusalCause.NonInlinableOrOffList, "<script>", region);
            return null;
        }

        return current;
    }

    // The resource-loading attributes the floor detects on ANY element (ADR-047 §3.3 item 5(a)).
    private static readonly string[] ResourceAttributes = ["src", "srcset", "background", "poster", "data", "ping"];

    /// <summary>
    /// The first resource-loading reference carried by <paramref name="tag"/>, detected by its
    /// resource-loading <b>attribute</b> on any element (ADR-047 §3.3 item 5(a)): <c>src</c>,
    /// <c>srcset</c>, <c>background</c>, <c>poster</c>, <c>data</c>, <c>ping</c>, <c>xlink:href</c>,
    /// the <c>href</c> of a resource element (<c>&lt;link&gt;</c>, <c>&lt;base&gt;</c>, SVG
    /// <c>&lt;image&gt;</c>/<c>&lt;use&gt;</c> — never an <c>&lt;a&gt;</c> navigation link, which is
    /// not a resource load, ADR-047 §3.3 item 4), or a <c>&lt;meta http-equiv="refresh"&gt;</c>.
    /// Returns <see langword="null"/> when the tag carries none. The deciding attributes are
    /// decoded before detection (ADR-047 §3.3 item 5(b)).
    /// </summary>
    /// <summary>
    /// The image-source attributes of a repeated part's tag whose value is an image data URI and
    /// may pass through: the <c>src</c> of an <c>img</c> and the <c>href</c> / <c>xlink:href</c>
    /// of an SVG <c>image</c>. A value larger than <see cref="ImageDataUri.MaximumBytes"/> is
    /// refused with the attribute, the record and the size, never the value.
    /// </summary>
    private IReadOnlyCollection<string> AdmittedImageSources(string name, string tag, string region, out HtmlRefusal? refusal)
    {
        refusal = null;
        var colon = name.LastIndexOf(':');
        var local = colon < 0 ? name : name[(colon + 1)..];
        string[] positions = local switch
        {
            "img" => ["src"],
            "image" => ["href", "xlink:href"],
            _ => [],
        };

        var admitted = new List<string>(positions.Length);
        foreach (var attribute in positions)
        {
            var value = Attribute(tag, attribute);
            if (value is null)
            {
                continue;
            }

            var decoded = System.Net.WebUtility.HtmlDecode(value);
            if (!ImageDataUri.Is(decoded))
            {
                continue;
            }

            var size = Encoding.UTF8.GetByteCount(decoded);
            if (size > ImageDataUri.MaximumBytes)
            {
                refusal = new HtmlRefusal(HtmlRefusalCause.ImageBytes, attribute, region, repeatedOrdinal, size);
                return [];
            }

            admitted.Add(attribute);
        }

        return admitted;
    }

    private static string? FirstResourceReference(string name, string tag) =>
        FirstResourceReference(name, tag, []);

    private static string? FirstResourceReference(string name, string tag, IReadOnlyCollection<string> admitted)
    {
        if (name == "meta")
        {
            var url = MetaRefreshUrl(tag);
            if (url is not null)
            {
                return url;
            }

            return IsMetaRefresh(tag) ? "refresh" : null;
        }

        foreach (var attribute in ResourceAttributes)
        {
            if (attribute == "src" && admitted.Contains("src"))
            {
                continue;
            }

            var value = Attribute(tag, attribute);
            if (value is not null)
            {
                return value;
            }
        }

        if (name is "link" or "base" or "image" or "use" && !admitted.Contains("href"))
        {
            var href = Attribute(tag, "href");
            if (href is not null)
            {
                return href;
            }
        }

        return admitted.Contains("xlink:href") ? null : Attribute(tag, "xlink:href");
    }

    /// <summary>
    /// Returns the target URL of a <c>&lt;meta http-equiv="refresh" content="…;url=…"&gt;</c>, or
    /// <see langword="null"/> when the tag is not a refresh with a URL. A refresh URL loads/navigates
    /// when the document is opened, so it is a resource reference off the closed inlinable list.
    /// </summary>
    private static string? MetaRefreshUrl(string tag)
    {
        if (!IsMetaRefresh(tag))
        {
            return null;
        }

        var content = Attribute(tag, "content");
        if (content is null)
        {
            return null;
        }

        // Decode before detecting the url= target, so an entity-/percent-encoded refresh URL is
        // classified as its decoded form (ADR-047 §3.3 item 5(b)).
        content = HtmlAssetInliner.DecodeValue(content);
        var marker = content.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var url = content[(marker + "url=".Length)..].Trim().Trim('"', '\'');
        return url.Length == 0 ? null : url;
    }

    /// <summary>
    /// Whether <paramref name="tag"/> is a <c>&lt;meta http-equiv="refresh"&gt;</c>, deciding on the
    /// decoded <c>http-equiv</c> value (ADR-047 §3.3 item 5(b)). A refresh meta loads/navigates when
    /// the document opens, so it is a resource reference off the closed inlinable list.
    /// </summary>
    private static bool IsMetaRefresh(string tag)
    {
        var httpEquiv = Attribute(tag, "http-equiv");
        return httpEquiv is not null
            && HtmlAssetInliner.DecodeValue(httpEquiv).Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase);
    }

    private static HtmlRefusal OffList(string reference, string region)
    {
        var cause = HtmlAssetInliner.ClassifyExternal(reference) ?? HtmlRefusalCause.NonInlinableOrOffList;
        return new HtmlRefusal(cause, reference, region);
    }

    private static bool HasCssResource(string css) =>
        css.Contains("url(", StringComparison.OrdinalIgnoreCase) || css.Contains("@import", StringComparison.OrdinalIgnoreCase);

    private static bool IsStylesheetRel(string rel) =>
        rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => string.Equals(token, "stylesheet", StringComparison.OrdinalIgnoreCase));

    private static bool IsStyleBlockStart(string html, int at)
    {
        const string marker = "<style";
        if (!StartsWithIgnoreCase(html, at, marker))
        {
            return false;
        }

        var after = at + marker.Length;
        return after >= html.Length || html[after] == '>' || html[after] == '/' || char.IsWhiteSpace(html[after]);
    }

    private static bool StartsWithIgnoreCase(string html, int at, string marker)
    {
        if (at + marker.Length > html.Length)
        {
            return false;
        }

        return html.AsSpan(at, marker.Length).Equals(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOfIgnoreCase(string html, string marker, int from) =>
        html.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the index of the <c>&gt;</c> that closes the tag opened at <paramref name="start"/>,
    /// skipping any <c>&gt;</c> that sits inside a single- or double-quoted attribute value.
    /// </summary>
    private static int FindTagEnd(string html, int start)
    {
        var i = start + 1;
        while (i < html.Length)
        {
            var c = html[i];
            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                while (i < html.Length && html[i] != quote)
                {
                    i++;
                }

                if (i < html.Length)
                {
                    i++;
                }

                continue;
            }

            if (c == '>')
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    // An attribute value in a double-quoted, single-quoted, or valid HTML5 unquoted form
    // (unquoted ends at whitespace or the tag's '>'): a reader that saw only quoted values
    // left unquoted ones invisible, so they passed unrefused and un-inlined (V-Bounce 4).
    private const string AttributeValue = """(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s"'>]+))""";
    private const string AttributeValueNoCapture = """(?:"[^"]*"|'[^']*'|[^\s"'>]+)""";

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(
            tag,
            """(?<![-\w:])""" + Regex.Escape(name) + """\s*=\s*""" + AttributeValue,
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static string ReplaceAttributeValue(string tag, string name, string value)
    {
        var match = Regex.Match(
            tag,
            """(?<![-\w:])""" + Regex.Escape(name) + """\s*=\s*""" + AttributeValueNoCapture,
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return tag;
        }

        // Rewrite in quoted form regardless of the source form — a data: URI or inlined CSS
        // always goes into a double-quoted attribute.
        return tag[..match.Index] + name + "=\"" + value + "\"" + tag[(match.Index + match.Length)..];
    }

    [GeneratedRegex("<(?<name>[a-zA-Z:]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ElementNamePattern();
}
