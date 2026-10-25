using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace TwoPassSpike;

/// <summary>A rectangle in points, origin at the page's top-left corner, y downwards.</summary>
internal sealed record Rect(double X0, double Y0, double X1, double Y1)
{
    public double YCentre => (Y0 + Y1) / 2;
}

/// <summary>One item row as the second pass reads it from its markers.</summary>
internal sealed class ReadBackRow
{
    public int Seq { get; set; }

    public int StartPage { get; set; }

    public Rect? StartRect { get; set; }

    public int EndPage { get; set; }

    /// <summary>The lowest "e" rectangle on the last page that holds an "e" marker: the row's last line.</summary>
    public Rect? EndRect { get; set; }

    public int EndRectPage { get; set; }

    public int? AmountMarkerPage { get; set; }

    public int StartMarkers { get; set; }

    public int EndMarkers { get; set; }

    public int AmountMarkers { get; set; }
}

/// <summary>The second pass's own reading of the markers, written before anything is stamped.</summary>
internal sealed class ReadBack
{
    public List<ReadBackRow> Rows { get; set; } = [];

    public int AnnotationsRead { get; set; }

    public int MarkerAnnotations { get; set; }

    public int UnknownAnnotations { get; set; }

    public int PagesRead { get; set; }

    /// <summary>Annotations that are not row markers, kept for the record (expected none).</summary>
    public List<string> Unknown { get; set; } = [];
}

/// <summary>
/// Reads the row markers back from a PDF with the PDF library's low-level
/// dictionary layer only: per page the /Annots array (direct or referenced), per
/// entry the /Link dictionary with a /URI action, its address parsed by a fixed
/// pattern, and its /Rect converted to top-left coordinates with the page's own
/// box height. It can also remove the markers.
/// </summary>
internal static partial class MarkerReader
{
    public static ReadBack Read(PdfDocument document)
    {
        var result = new ReadBack { PagesRead = document.PageCount };
        var rows = new SortedDictionary<int, (List<(int Page, Rect Rect)> S, List<(int Page, Rect Rect)> E, List<(int Page, Rect Rect)> A)>();
        for (var index = 0; index < document.PageCount; index++)
        {
            var page = document.Pages[index];
            var pageNumber = index + 1;
            var top = PageTop(page);
            var annotations = Resolve(page.Elements["/Annots"]) as PdfArray;
            if (annotations is null)
            {
                continue;
            }

            for (var i = 0; i < annotations.Elements.Count; i++)
            {
                result.AnnotationsRead++;
                var annotation = Resolve(annotations.Elements[i]) as PdfDictionary;
                var marker = annotation is null ? null : Marker(annotation);
                if (marker is null)
                {
                    result.UnknownAnnotations++;
                    if (result.Unknown.Count < 20)
                    {
                        result.Unknown.Add($"page {pageNumber}: {Describe(annotation)}");
                    }

                    continue;
                }

                var (sequence, kind) = marker.Value;
                var rect = ReadRect(annotation!, top);
                if (rect is null)
                {
                    result.UnknownAnnotations++;
                    result.Unknown.Add($"page {pageNumber}: marker {sequence}/{kind} without a readable /Rect");
                    continue;
                }

                result.MarkerAnnotations++;
                if (!rows.TryGetValue(sequence, out var lists))
                {
                    lists = (new List<(int Page, Rect Rect)>(), new List<(int Page, Rect Rect)>(), new List<(int Page, Rect Rect)>());
                    rows[sequence] = lists;
                }

                (kind switch { 's' => lists.S, 'e' => lists.E, _ => lists.A }).Add((pageNumber, rect));
            }
        }

        foreach (var (sequence, (s, e, a)) in rows)
        {
            var row = new ReadBackRow
            {
                Seq = sequence,
                StartMarkers = s.Count,
                EndMarkers = e.Count,
                AmountMarkers = a.Count,
            };
            if (s.Count > 0)
            {
                row.StartPage = s[0].Page;
                row.StartRect = s[0].Rect;
            }

            if (e.Count > 0)
            {
                var lastEPage = e.Max(x => x.Page);
                row.EndRectPage = lastEPage;
                row.EndRect = e.Where(x => x.Page == lastEPage).OrderBy(x => x.Rect.Y1).Last().Rect;
            }

            if (a.Count > 0)
            {
                row.AmountMarkerPage = a.Max(x => x.Page);
            }

            row.EndPage = Math.Max(row.EndRectPage, row.AmountMarkerPage ?? 0);
            result.Rows.Add(row);
        }

        return result;
    }

    /// <summary>
    /// The guard: the sequences read equal the fixture's item sequences, each with
    /// exactly one "s", at least one "e" (and exactly one "a" in strategy "cell");
    /// start page not after end page; at most one row continues across any page
    /// break. Returns the reasons it fails, empty when it holds.
    /// </summary>
    public static List<string> Guard(ReadBack readBack, IReadOnlyList<int> expectedSequences, string strategy)
    {
        var reasons = new List<string>();
        var read = readBack.Rows.Select(r => r.Seq).ToHashSet();
        var expected = expectedSequences.ToHashSet();
        var missing = expectedSequences.Where(s => !read.Contains(s)).ToList();
        var extra = readBack.Rows.Select(r => r.Seq).Where(s => !expected.Contains(s)).ToList();
        if (missing.Count > 0)
        {
            reasons.Add($"{missing.Count} row(s) without markers, first {missing[0]}");
        }

        if (extra.Count > 0)
        {
            reasons.Add($"{extra.Count} marker sequence(s) not in the block, first {extra[0]}");
        }

        foreach (var row in readBack.Rows.Where(r => expected.Contains(r.Seq)))
        {
            if (row.StartMarkers != 1 || row.EndMarkers < 1 || (strategy == HtmlBuilder.Cell ? row.AmountMarkers != 1 : row.AmountMarkers != 0))
            {
                reasons.Add($"row {row.Seq}: {row.StartMarkers} start, {row.EndMarkers} end, {row.AmountMarkers} amount marker(s)");
            }
            else if (row.StartPage > row.EndPage)
            {
                reasons.Add($"row {row.Seq}: starts on page {row.StartPage} after it ends on page {row.EndPage}");
            }

            if (reasons.Count >= 20)
            {
                break;
            }
        }

        var lastPage = readBack.PagesRead;
        for (var page = 1; page < lastPage; page++)
        {
            var crossing = readBack.Rows.Count(r => r.StartPage <= page && r.EndPage > page);
            if (crossing > 1)
            {
                reasons.Add($"{crossing} rows continue across the break after page {page}");
                break;
            }
        }

        return reasons;
    }

    /// <summary>Removes every row-marker annotation; drops the /Annots key of a page whose array ends empty. Returns how many were removed.</summary>
    public static int RemoveMarkers(PdfDocument document)
    {
        var removed = 0;
        for (var index = 0; index < document.PageCount; index++)
        {
            var page = document.Pages[index];
            if (Resolve(page.Elements["/Annots"]) is not PdfArray annotations)
            {
                continue;
            }

            for (var i = annotations.Elements.Count - 1; i >= 0; i--)
            {
                if (Resolve(annotations.Elements[i]) is PdfDictionary annotation && Marker(annotation) is not null)
                {
                    annotations.Elements.RemoveAt(i);
                    removed++;
                }
            }

            if (annotations.Elements.Count == 0)
            {
                page.Elements.Remove("/Annots");
            }
        }

        return removed;
    }

    /// <summary>The top of the page box (its fourth number), read from the page's own /MediaBox.</summary>
    public static double PageTop(PdfPage page)
    {
        if (Resolve(page.Elements["/MediaBox"]) is PdfArray box && box.Elements.Count == 4 && Number(box.Elements[3]) is { } top)
        {
            return top;
        }

        return page.Height.Point;
    }

    /// <summary>The (sequence, kind) of a row-marker annotation, or null for any other annotation.</summary>
    private static (int Sequence, char Kind)? Marker(PdfDictionary annotation)
    {
        if (Name(annotation.Elements["/Subtype"]) != "/Link" || Resolve(annotation.Elements["/A"]) is not PdfDictionary action)
        {
            return null;
        }

        if (Name(action.Elements["/S"]) != "/URI" || Text(action.Elements["/URI"]) is not { } address)
        {
            return null;
        }

        var match = MarkerPattern().Match(address);
        return match.Success ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value[0]) : null;
    }

    private static Rect? ReadRect(PdfDictionary annotation, double top)
    {
        if (Resolve(annotation.Elements["/Rect"]) is not PdfArray array || array.Elements.Count != 4)
        {
            return null;
        }

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (Number(array.Elements[i]) is not { } value)
            {
                return null;
            }

            values[i] = value;
        }

        var x0 = Math.Min(values[0], values[2]);
        var x1 = Math.Max(values[0], values[2]);
        var yLow = Math.Min(values[1], values[3]);
        var yHigh = Math.Max(values[1], values[3]);
        return new Rect(x0, top - yHigh, x1, top - yLow);
    }

    private static string Describe(PdfDictionary? annotation) =>
        annotation is null ? "not a dictionary" : $"subtype {Name(annotation.Elements["/Subtype"]) ?? "none"}";

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static string? Name(PdfItem? item) => Resolve(item) switch
    {
        PdfName name => name.Value,
        PdfNameObject name => name.Value,
        _ => null,
    };

    private static string? Text(PdfItem? item) => Resolve(item) switch
    {
        PdfString text => text.Value,
        PdfStringObject text => text.Value,
        _ => null,
    };

    private static double? Number(PdfItem? item) => Resolve(item) switch
    {
        PdfReal real => real.Value,
        PdfInteger integer => integer.Value,
        PdfLongInteger integer => integer.Value,
        PdfRealObject real => real.Value,
        PdfIntegerObject integer => integer.Value,
        _ => null,
    };

    [GeneratedRegex(@"^https://m\.invalid/r/(\d{1,8})/(s|e|a)$")]
    private static partial Regex MarkerPattern();
}
