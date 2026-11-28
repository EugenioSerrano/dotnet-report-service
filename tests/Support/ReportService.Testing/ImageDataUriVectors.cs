namespace ReportService.Testing;

/// <summary>
/// One case of the image data URI definition: a value, whether it is an image data URI, and
/// the clause of the definition it exercises. <see cref="Decoded"/> is the value as a template
/// sees it; <see cref="Written"/> is how it stands between the quotes of a JSON document,
/// which differs from the decoded value only for the cases that exercise escapes.
/// </summary>
/// <param name="Clause">The clause of the definition the case exercises.</param>
/// <param name="Decoded">The value once its JSON escapes are resolved.</param>
/// <param name="Written">The value as written in JSON, between its quotes.</param>
/// <param name="IsImageDataUri">Whether the value is an image data URI.</param>
public sealed record ImageDataUriVector(string Clause, string Decoded, string Written, bool IsImageDataUri)
{
    /// <inheritdoc />
    public override string ToString() => Clause;
}

/// <summary>
/// The conformance cases of the image data URI definition, shared by every classifier of the
/// product that decides whether a value is one: the row bound's classifier of a record value,
/// and the render-time classifier of an image source. Each case is taken from the definition's
/// text - the scheme <c>data:</c>; a listed media type (<c>image/png</c>, <c>image/jpeg</c>,
/// <c>image/gif</c>, <c>image/webp</c>, <c>image/svg+xml</c>); zero or more <c>;name=value</c>
/// parameters; <c>;base64</c>; a comma; a body of the base64 alphabet with at most two
/// trailing <c>=</c>, a length that is a multiple of four and no whitespace; scheme, media
/// type and <c>base64</c> case-insensitive - and never from either implementation.
/// </summary>
public static class ImageDataUriVectors
{
    private const string Body = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    /// <summary>Every case, the ones that are image data URIs first.</summary>
    public static IReadOnlyList<ImageDataUriVector> All { get; } =
    [
        Plain("listed type image/png", "data:image/png;base64," + Body, true),
        Plain("listed type image/jpeg", "data:image/jpeg;base64,/9j/4AAQ", true),
        Plain("listed type image/gif", "data:image/gif;base64,R0lGODlh", true),
        Plain("listed type image/webp", "data:image/webp;base64,UklGRg==", true),
        Plain("listed type image/svg+xml", "data:image/svg+xml;base64,PHN2Zz4=", true),
        Plain("scheme, type and base64 match case-insensitively", "DATA:Image/PNG;BASE64,iVBORw==", true),
        Plain("one parameter before base64", "data:image/png;name=logo;base64,iVBORw==", true),
        Plain("two parameters before base64", "data:image/svg+xml;charset=utf-8;name=a;base64,PHN2Zz4=", true),
        Plain("body with one trailing =", "data:image/png;base64,iVBORwA=", true),
        Plain("body with + and / of the alphabet", "data:image/png;base64,ab+/cd+/", true),
        Escaped("a solidus written as an escape", "data:image/png;base64,ab+/cd+/", "data:image\\/png;base64,ab+\\/cd+\\/", true),
        Escaped("a plus written as a unicode escape", "data:image/png;base64,ab+/", "data:image/png;base64,ab\\u002B/", true),

        Plain("another media type: application/pdf", "data:application/pdf;base64,JVBERi0=", false),
        Plain("an image type not listed: image/bmp", "data:image/bmp;base64,Qk0=", false),
        Plain("an image type not listed: image/svg", "data:image/svg;base64,PHN2Zz4=", false),
        Plain("no base64 token: percent-encoded data URI", "data:image/png,%89PNG", false),
        Plain("no base64 token: svg as text", "data:image/svg+xml,<svg></svg>", false),
        Plain("base64 token not last", "data:image/png;base64;name=a,iVBORw==", false),
        Plain("a parameter without a value", "data:image/png;name;base64,iVBORw==", false),
        Plain("a parameter without a name", "data:image/png;=a;base64,iVBORw==", false),
        Plain("body length not a multiple of four", "data:image/png;base64,iVBOR", false),
        Plain("body with three trailing =", "data:image/png;base64,iVBO====", false),
        Plain("= inside the body", "data:image/png;base64,iV=ORw==", false),
        Plain("whitespace inside the body", "data:image/png;base64,iVBO Rw==", false),
        Plain("a line break inside the body", "data:image/png;base64,iVBO\nRw==", false),
        Plain("a character outside the alphabet", "data:image/png;base64,iVBO-w==", false),
        Plain("a URL-safe alphabet character", "data:image/png;base64,iVBO_w==", false),
        Plain("whitespace before the scheme", " data:image/png;base64,iVBORw==", false),
        Plain("a scheme other than data", "http://example.com/a.png", false),
        Plain("an empty value", string.Empty, false),
        Plain("scheme only", "data:", false),
        Plain("no comma", "data:image/png;base64", false),
        Plain("a non-ASCII character in the body", "data:image/png;base64,iVBOéw==", false),
    ];

    private static ImageDataUriVector Plain(string clause, string value, bool expected) =>
        new(clause, value, value.Replace("\n", "\\n", StringComparison.Ordinal), expected);

    private static ImageDataUriVector Escaped(string clause, string decoded, string written, bool expected) =>
        new(clause, decoded, written, expected);
}
