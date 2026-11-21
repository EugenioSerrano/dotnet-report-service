using System.Globalization;
using System.Text.Json;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.PdfStructure.Tests;

/// <summary>
/// The committed golden documents and the companions each one is read against:
/// the golden PDF itself, the source fixture snapshot it was printed from, and
/// the page/frame declaration that states its paper, margins and columns. The
/// catalog enumerates them from the output folder they are copied to, so a
/// golden added to <c>Goldens/</c> is a new case without a code change.
/// </summary>
internal static class GoldenCatalog
{
    /// <summary>The folder the committed goldens and their companions are copied to.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Goldens");

    /// <summary>Every committed golden, by the stem its three files share.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        .. System.IO.Directory
            .EnumerateFiles(Directory, "*.pdf", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal),
    ];

    /// <summary>The golden stems as a theory source.</summary>
    public static TheoryData<string> Goldens
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>The golden PDF file of <paramref name="name"/>.</summary>
    public static string PdfPath(string name) => Path.Combine(Directory, name + ".pdf");

    /// <summary>The page/frame declaration of <paramref name="name"/>.</summary>
    public static GoldenExpectation Expectation(string name) =>
        GoldenExpectation.Load(Path.Combine(Directory, name + ".expectation.json"));

    /// <summary>
    /// The row keys of the source fixture <paramref name="name"/> was printed from,
    /// in the order the snapshot holds them: the values the row-key column must carry,
    /// read from the source and never from the printed document.
    /// </summary>
    public static IReadOnlyList<string> SourceRowKeys(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, name + ".fixture.json")));
        var rowKey = Expectation(name).RowKey;
        var keys = new List<string>();
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.TryGetProperty(rowKey, out var value))
            {
                keys.Add(value.ValueKind == JsonValueKind.Number
                    ? value.GetRawText()
                    : value.GetString() ?? string.Empty);
            }
        }

        return keys;
    }

    /// <summary>Reads <paramref name="name"/> in-process with the product reader, at the declared margins.</summary>
    public static PdfReading Read(string name)
    {
        var expectation = Expectation(name);
        return PdfWordReader.Read(PdfPath(name), PlacementConvention.FromMillimetres(expectation.Margins));
    }

    /// <summary>
    /// The row keys as the printed document carries them, in painting order across the
    /// pages: the content-band words that fall in the row-key column and read as a whole
    /// number. The repeated column heading ("Seq") is not a number and is excluded, and
    /// the other columns lie outside the row-key column's horizontal span.
    /// </summary>
    public static IReadOnlyList<string> PrintedRowKeys(PdfReading reading, GoldenExpectation expectation)
    {
        // A cell's glyphs can start a hair left of the column's declared edge (the font's
        // left side bearing), so the column span is taken with a one-line tolerance; the
        // next column is far to the right, so no other cell is caught.
        var (startPt, endPt) = expectation.RowKeyColumnPoints();
        startPt -= PlacementConvention.LineTolerancePoints;
        var keys = new List<string>();
        foreach (var page in reading.Pages)
        {
            foreach (var word in page.Words)
            {
                if (word.Band != ReadingBand.Content)
                {
                    continue;
                }

                if (word.Left < startPt || word.Left >= endPt)
                {
                    continue;
                }

                if (long.TryParse(word.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    keys.Add(word.Text);
                }
            }
        }

        return keys;
    }
}
