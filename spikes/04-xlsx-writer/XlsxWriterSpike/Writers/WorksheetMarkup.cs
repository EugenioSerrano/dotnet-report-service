using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;
using XlsxWriterSpike.Typing;

namespace XlsxWriterSpike.Writers;

/// <summary>
/// SpreadsheetML written with <see cref="OpenXmlWriter"/>, shared by the three
/// Open XML SDK variants: inline-string text cells, numeric cells with the
/// invariant number text, never a formula element and no shared-strings part.
/// </summary>
internal static class WorksheetMarkup
{
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Writes a complete worksheet part: header row, then one row per record.</summary>
    public static void WriteWorksheet(OpenXmlWriter writer, ColumnProfile profile, IEnumerable<TypedRow> rows)
    {
        var letters = profile.Columns.Select((_, i) => ColumnLetters(i)).ToArray();
        var worksheet = new Worksheet();
        var sheetData = new SheetData();
        var row = new Row();
        var cell = new Cell();
        var inline = new InlineString();
        var text = new Text();
        var value = new CellValue();
        var rowAttributes = new List<OpenXmlAttribute>(1);
        var cellAttributes = new List<OpenXmlAttribute>(2);
        var preserve = new[] { new OpenXmlAttribute("xml", "space", XmlNamespace, "preserve") };
        var noAttributes = Array.Empty<OpenXmlAttribute>();

        writer.WriteStartDocument();
        writer.WriteStartElement(worksheet);
        writer.WriteStartElement(sheetData);

        long rowNumber = 1;
        StartRow(rowNumber);
        for (var i = 0; i < profile.Columns.Count; i++)
        {
            WriteText(i, rowNumber, profile.Columns[i].Name);
        }

        writer.WriteEndElement();

        foreach (var typed in rows)
        {
            rowNumber++;
            StartRow(rowNumber);
            for (var i = 0; i < typed.Cells.Length; i++)
            {
                ref var c = ref typed.Cells[i];
                switch (c.Kind)
                {
                    case CellKind.Text:
                        WriteText(i, rowNumber, c.Text!);
                        break;
                    case CellKind.Number:
                        cellAttributes.Clear();
                        cellAttributes.Add(new OpenXmlAttribute("r", string.Empty, Reference(i, rowNumber)));
                        writer.WriteStartElement(cell, cellAttributes);
                        writer.WriteStartElement(value, noAttributes);
                        writer.WriteString(c.Text!);
                        writer.WriteEndElement();
                        writer.WriteEndElement();
                        break;
                    default:
                        break;
                }
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        void StartRow(long number)
        {
            rowAttributes.Clear();
            rowAttributes.Add(new OpenXmlAttribute("r", string.Empty, number.ToString(CultureInfo.InvariantCulture)));
            writer.WriteStartElement(row, rowAttributes);
        }

        void WriteText(int column, long number, string content)
        {
            cellAttributes.Clear();
            cellAttributes.Add(new OpenXmlAttribute("r", string.Empty, Reference(column, number)));
            cellAttributes.Add(new OpenXmlAttribute("t", string.Empty, "inlineStr"));
            writer.WriteStartElement(cell, cellAttributes);
            writer.WriteStartElement(inline, noAttributes);
            var needsPreserve = content.Length > 0 && (char.IsWhiteSpace(content[0]) || char.IsWhiteSpace(content[^1]));
            writer.WriteStartElement(text, needsPreserve ? preserve : noAttributes);
            writer.WriteString(content);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        string Reference(int column, long number) => string.Concat(letters[column], number.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A stylesheet with the single default cell format.</summary>
    public static Stylesheet CreateStylesheet() => new(
        new Fonts(new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" })) { Count = 1 },
        new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
        new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 },
        new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
        new CellFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 }) { Count = 1 });

    /// <summary>The workbook part content: the sheets with their relationship ids.</summary>
    public static Workbook CreateWorkbook(IReadOnlyList<(string Name, string RelationshipId)> sheets)
    {
        var list = new Sheets();
        for (var i = 0; i < sheets.Count; i++)
        {
            list.Append(new Sheet { Name = sheets[i].Name, SheetId = (uint)(i + 1), Id = sheets[i].RelationshipId });
        }

        return new Workbook(list);
    }

    /// <summary>Column letters for a 0-based column index (A, B, ..., Z, AA, ...).</summary>
    public static string ColumnLetters(int index)
    {
        var letters = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + ((n - 1) % 26)) + letters;
        }

        return letters;
    }
}
