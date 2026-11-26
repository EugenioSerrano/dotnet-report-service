using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace ReportService.Pdf.Verification;

/// <summary>A page's media box, in the points the document declares.</summary>
/// <param name="Left">The box's left edge.</param>
/// <param name="Bottom">The box's bottom edge.</param>
/// <param name="Right">The box's right edge.</param>
/// <param name="Top">The box's top edge.</param>
/// <param name="Width">The box's width.</param>
/// <param name="Height">The box's height.</param>
/// <param name="Rotation">The page's rotation, in degrees.</param>
public sealed record PageBox(double Left, double Bottom, double Right, double Top, double Width, double Height, int Rotation);

/// <summary>
/// A page's own dictionary, rendered as the stable text the assembly verification
/// compares: every key with a canonical rendering of its value. The rendering
/// carries document structure only, never a consumer's value.
/// </summary>
/// <param name="Entries">The page dictionary's entries.</param>
public sealed record PageDictionary(IReadOnlyDictionary<string, string> Entries);

/// <summary>One page of a document read with its box and dictionary, beside the words the checks read.</summary>
/// <param name="Number">The page's number, from one.</param>
/// <param name="Page">The page as the word reader returns it.</param>
/// <param name="Box">The page's media box.</param>
/// <param name="Dictionary">The page's own dictionary.</param>
public sealed record DocumentPage(int Number, PrintedPage Page, PageBox Box, PageDictionary Dictionary);

/// <summary>A whole printed document read with its pages' boxes and dictionaries.</summary>
/// <param name="Pages">The pages, in order.</param>
public sealed record PdfDocumentReading(IReadOnlyList<DocumentPage> Pages);

/// <summary>
/// The additive document reading of the product's PDF reader: the pages' words as
/// the block checks already read them, plus each page's media box and its own
/// dictionary. The per-block reading path is unchanged; the assembly verification
/// is what needs the two additions.
/// </summary>
public static class PdfDocumentReader
{
    /// <summary>Reads the document at <paramref name="path"/> with its pages' boxes and dictionaries.</summary>
    /// <param name="path">The file of the printed document.</param>
    /// <param name="margins">The template's declared margins, in points.</param>
    /// <returns>The pages of the document.</returns>
    /// <exception cref="PdfUnreadableException">The document cannot be opened or read.</exception>
    public static PdfDocumentReading Read(string path, PageMargins margins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(margins);

        try
        {
            using var document = PdfDocument.Open(path);
            var pages = new List<DocumentPage>(document.NumberOfPages);
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                var page = document.GetPage(number);
                pages.Add(new DocumentPage(
                    number,
                    PdfWordReader.ReadPage(page, number, margins),
                    BoxOf(page),
                    DictionaryOf(page)));
            }

            return new PdfDocumentReading(pages);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new PdfUnreadableException(exception);
        }
    }

    private static PageBox BoxOf(Page page)
    {
        var bounds = page.MediaBox.Bounds;
        return new PageBox(
            bounds.Left,
            bounds.Bottom,
            bounds.Right,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            page.Rotation.Value);
    }

    private static PageDictionary DictionaryOf(Page page)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, token) in page.Dictionary.Data)
        {
            entries[key] = TokenText.Of(token);
        }

        return new PageDictionary(entries);
    }

    /// <summary>
    /// The canonical text of a page dictionary's value: names, numbers, strings,
    /// booleans, references, arrays and nested dictionaries, with a fixed ordering
    /// for a nested dictionary's keys. The text is compared between a source page
    /// and the page the assembly wrote; a value that differs for a reason the
    /// architecture does not allow fails the assembly.
    /// </summary>
    private static class TokenText
    {
        public static string Of(IToken? token) => token switch
        {
            null => "null",
            NullToken => "null",
            BooleanToken boolean => boolean.Data ? "true" : "false",
            NumericToken numeric => numeric.Data.ToString("R", CultureInfo.InvariantCulture),
            StringToken text => "(" + text.Data + ")",
            HexToken hex => "<" + Convert.ToHexStringLower(hex.Bytes) + ">",
            NameToken name => "/" + name.Data,
            IndirectReferenceToken reference => "R:" + reference.Data.ObjectNumber.ToString(CultureInfo.InvariantCulture)
                + ":" + reference.Data.Generation.ToString(CultureInfo.InvariantCulture),
            ArrayToken array => "[" + string.Join(' ', array.Data.Select(Of)) + "]",
            DictionaryToken dictionary => Dictionary(dictionary),
            StreamToken stream => "stream" + Dictionary(stream.StreamDictionary),
            _ => token.GetType().Name,
        };

        private static string Dictionary(DictionaryToken dictionary)
        {
            var builder = new StringBuilder("<<");
            foreach (var (key, value) in dictionary.Data.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                builder.Append(key).Append(' ').Append(Of(value)).Append(' ');
            }

            return builder.Append(">>").ToString();
        }
    }
}
