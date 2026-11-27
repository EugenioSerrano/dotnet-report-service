using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Tokens;

namespace ReportService.Pdf.Verification.PageCodes;

/// <summary>One form object a page's resources hold.</summary>
/// <param name="Name">Its resource name, without the slash.</param>
/// <param name="BoundingBox">Its <c>/BBox</c>, when it has a four-number one.</param>
/// <param name="Content">Its content stream, decoded.</param>
public sealed record PageForm(string Name, IReadOnlyList<double>? BoundingBox, byte[] Content);

/// <summary>One drawing of a named object, with the transformation in force when it was drawn.</summary>
/// <param name="Name">The object's resource name, without the slash.</param>
/// <param name="Matrix">The current transformation matrix <c>[a b c d e f]</c>.</param>
public sealed record ObjectDrawing(string Name, IReadOnlyList<double> Matrix);

/// <summary>
/// What a page draws beside its text, as the assembly verification needs it: the
/// names in each resource category, the form objects with their bounding boxes and
/// decoded content, and every named-object drawing with its matrix. It reads the
/// structure only; it never interprets what a form draws.
/// </summary>
/// <param name="ResourceNames">The resource names by category (<c>Font</c>, <c>XObject</c>, ...).</param>
/// <param name="Forms">The form objects of the page's resources.</param>
/// <param name="Drawings">The named-object drawings, in painting order.</param>
public sealed record PageGraphics(
    IReadOnlyDictionary<string, IReadOnlyList<string>> ResourceNames,
    IReadOnlyList<PageForm> Forms,
    IReadOnlyList<ObjectDrawing> Drawings)
{
    /// <summary>A page that draws nothing beside its text.</summary>
    public static PageGraphics Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), [], []);
}

/// <summary>Reads a page's <see cref="PageGraphics"/>.</summary>
public static class PageGraphicsReader
{
    /// <summary>Reads one page.</summary>
    /// <param name="document">The open document.</param>
    /// <param name="page">The page.</param>
    /// <returns>Its graphics.</returns>
    public static PageGraphics Read(PdfDocument document, Page page)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);

        var names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var forms = new List<PageForm>();
        if (page.Dictionary.Data.TryGetValue("Resources", out var resourcesToken)
            && Resolve(document, resourcesToken) is DictionaryToken resources)
        {
            foreach (var (category, categoryToken) in resources.Data)
            {
                if (Resolve(document, categoryToken) is not DictionaryToken entries)
                {
                    continue;
                }

                names[category] = [.. entries.Data.Keys.Order(StringComparer.Ordinal)];
                if (category != "XObject")
                {
                    continue;
                }

                foreach (var (name, objectToken) in entries.Data)
                {
                    if (Resolve(document, objectToken) is StreamToken stream
                        && stream.StreamDictionary.Data.TryGetValue("Subtype", out var subtype)
                        && subtype is NameToken { Data: "Form" })
                    {
                        forms.Add(new PageForm(name, BoxOf(document, stream), Decode(stream)));
                    }
                }
            }
        }

        return new PageGraphics(names, forms, Drawings(page.Operations));
    }

    private static IReadOnlyList<ObjectDrawing> Drawings(IReadOnlyList<IGraphicsStateOperation> operations)
    {
        var drawings = new List<ObjectDrawing>();
        var stack = new Stack<double[]>();
        var current = new double[] { 1, 0, 0, 1, 0, 0 };
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case Push:
                    stack.Push((double[])current.Clone());
                    break;
                case Pop:
                    current = stack.Count > 0 ? stack.Pop() : new double[] { 1, 0, 0, 1, 0, 0 };
                    break;
                case ModifyCurrentTransformationMatrix modify when modify.Value is { Length: 6 } value:
                    current = Multiply(value, current);
                    break;
                case InvokeNamedXObject invoke:
                    drawings.Add(new ObjectDrawing(invoke.Name.Data, (double[])current.Clone()));
                    break;
            }
        }

        return drawings;
    }

    private static double[] Multiply(double[] m, double[] n) =>
    [
        (m[0] * n[0]) + (m[1] * n[2]),
        (m[0] * n[1]) + (m[1] * n[3]),
        (m[2] * n[0]) + (m[3] * n[2]),
        (m[2] * n[1]) + (m[3] * n[3]),
        (m[4] * n[0]) + (m[5] * n[2]) + n[4],
        (m[4] * n[1]) + (m[5] * n[3]) + n[5],
    ];

    private static IReadOnlyList<double>? BoxOf(PdfDocument document, StreamToken stream)
    {
        if (!stream.StreamDictionary.Data.TryGetValue("BBox", out var boxToken)
            || Resolve(document, boxToken) is not ArrayToken box
            || box.Data.Count != 4)
        {
            return null;
        }

        var values = new List<double>(4);
        foreach (var item in box.Data)
        {
            if (Resolve(document, item) is not NumericToken number)
            {
                return null;
            }

            values.Add(number.Double);
        }

        return values;
    }

    private static byte[] Decode(StreamToken stream) =>
        PdfExtensions.Decode(stream, DefaultFilterProvider.Instance).ToArray();

    private static IToken? Resolve(PdfDocument document, IToken? token)
    {
        var guard = 0;
        while (token is IndirectReferenceToken reference && guard++ < 8)
        {
            token = document.Structure.GetObject(reference.Data).Data;
        }

        return token;
    }
}
