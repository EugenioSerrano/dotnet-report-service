using System.IO.Compression;
using System.Text;
using System.Xml;

namespace XlsxWriterSpike.Inspection;

/// <summary>One cell as found in a worksheet part.</summary>
internal sealed class CellRecord
{
    /// <summary>0-based column index.</summary>
    public int Column { get; set; }

    /// <summary>The <c>r</c> attribute (or the computed reference).</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>The <c>t</c> attribute (<c>null</c> = numeric).</summary>
    public string? Type { get; set; }

    /// <summary>The value: inline text, resolved shared string, or the <c>v</c> text.</summary>
    public string? Text { get; set; }

    /// <summary>Whether the cell has an <c>f</c> element.</summary>
    public bool HasFormula { get; set; }

    /// <summary>Whether the cell is a text cell (<c>inlineStr</c>, <c>s</c> or <c>str</c>).</summary>
    public bool IsText => Type is "inlineStr" or "s" or "str";

    /// <summary>Whether the cell is a numeric cell (no <c>t</c>, or <c>n</c>).</summary>
    public bool IsNumber => Type is null or "n";
}

/// <summary>One row as found in a worksheet part.</summary>
internal sealed class SheetRow
{
    /// <summary>1-based row number.</summary>
    public long RowNumber { get; set; }

    /// <summary>Cells in document order.</summary>
    public List<CellRecord> Cells { get; } = [];
}

/// <summary>Counts gathered while a worksheet is read.</summary>
internal sealed class SheetInfo(string name, string partPath)
{
    /// <summary>Sheet name.</summary>
    public string Name { get; } = name;

    /// <summary>Part path inside the package.</summary>
    public string PartPath { get; } = partPath;

    /// <summary>Rows read (including the header).</summary>
    public long Rows { get; set; }

    /// <summary>Cells by <c>t</c> value (<c>n</c> for numeric).</summary>
    public SortedDictionary<string, long> CellsByType { get; } = new(StringComparer.Ordinal);

    /// <summary><c>f</c> elements.</summary>
    public long Formulas { get; set; }
}

/// <summary>
/// Reads an XLSX package with the BCL only (<see cref="ZipArchive"/> and
/// <see cref="XmlReader"/>), streaming the worksheets, so the inspection is
/// neutral to every writer under test.
/// </summary>
internal sealed class XlsxPackageReader : IDisposable
{
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly ZipArchive _zip;
    private List<string>? _sharedStrings;

    /// <summary>Opens the package and reads the workbook structure.</summary>
    public XlsxPackageReader(string path)
    {
        _zip = ZipFile.OpenRead(path);
        Parts = [.. _zip.Entries.Select(e => e.FullName)];
        var workbookPath = RelationshipTargets("_rels/.rels", string.Empty)
            .First(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target;
        var workbookFolder = workbookPath.Contains('/', StringComparison.Ordinal) ? workbookPath[..(workbookPath.LastIndexOf('/') + 1)] : string.Empty;
        var relationshipsPath = workbookFolder + "_rels/" + workbookPath[(workbookPath.LastIndexOf('/') + 1)..] + ".rels";
        var relationships = RelationshipTargets(relationshipsPath, workbookFolder).ToList();

        using (var xml = Open(workbookPath))
        {
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sheet")
                {
                    var id = xml.GetAttribute("id", RelationshipsNamespace);
                    var target = relationships.First(r => r.Id == id).Target;
                    Sheets.Add(new SheetInfo(xml.GetAttribute("name") ?? string.Empty, target));
                }
            }
        }

        var styles = relationships.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal));
        if (styles.Target is not null && Entry(styles.Target) is not null)
        {
            using var xml = Open(styles.Target);
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "cellXfs")
                {
                    CellXfsCount = xml.IsEmptyElement ? 0 : CountChildren(xml, "xf");
                    break;
                }
            }
        }

        var shared = relationships.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal));
        SharedStringsPath = shared.Target is not null && Entry(shared.Target) is not null ? shared.Target : null;
        if (SharedStringsPath is not null)
        {
            SharedStringsBytes = Entry(SharedStringsPath)!.Length;
            using var xml = Open(SharedStringsPath);
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sst")
                {
                    SharedStringsCount = xml.GetAttribute("count");
                    SharedStringsUniqueCount = xml.GetAttribute("uniqueCount");
                    break;
                }
            }
        }
    }

    /// <summary>Part names in the package.</summary>
    public IReadOnlyList<string> Parts { get; }

    /// <summary>Sheets in workbook order.</summary>
    public List<SheetInfo> Sheets { get; } = [];

    /// <summary>Number of <c>xf</c> in <c>cellXfs</c> (<c>null</c> when there is no styles part).</summary>
    public int? CellXfsCount { get; }

    /// <summary>Shared-strings part path, or <c>null</c> when absent.</summary>
    public string? SharedStringsPath { get; }

    /// <summary>Uncompressed size of the shared-strings part.</summary>
    public long SharedStringsBytes { get; }

    /// <summary><c>count</c> attribute of the shared-strings table.</summary>
    public string? SharedStringsCount { get; }

    /// <summary><c>uniqueCount</c> attribute of the shared-strings table.</summary>
    public string? SharedStringsUniqueCount { get; }

    /// <summary>Streams the rows of a sheet and fills its counts.</summary>
    public IEnumerable<SheetRow> ReadRows(SheetInfo sheet)
    {
        using var xml = Open(sheet.PartPath);
        SheetRow? current = null;
        long previousRow = 0;
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "row")
            {
                var r = xml.GetAttribute("r");
                previousRow = r is null ? previousRow + 1 : long.Parse(r, System.Globalization.CultureInfo.InvariantCulture);
                current = new SheetRow { RowNumber = previousRow };
                sheet.Rows++;
                if (xml.IsEmptyElement)
                {
                    yield return current;
                    current = null;
                }
            }
            else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "c" && current is not null)
            {
                var cell = ReadCell(xml, current.Cells.Count);
                current.Cells.Add(cell);
                var key = cell.Type ?? "n";
                sheet.CellsByType[key] = sheet.CellsByType.GetValueOrDefault(key) + 1;
                if (cell.HasFormula)
                {
                    sheet.Formulas++;
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "row" && current is not null)
            {
                yield return current;
                current = null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _zip.Dispose();

    private CellRecord ReadCell(XmlReader xml, int position)
    {
        var cell = new CellRecord
        {
            Reference = xml.GetAttribute("r") ?? string.Empty,
            Type = xml.GetAttribute("t"),
        };
        cell.Column = cell.Reference.Length > 0 ? ColumnIndex(cell.Reference) : position;
        string? value = null;
        var inline = new StringBuilder();
        var hasInline = false;
        if (!xml.IsEmptyElement)
        {
            using var sub = xml.ReadSubtree();
            sub.Read();
            sub.Read();
            var phonetic = false;
            while (!sub.EOF)
            {
                if (sub.NodeType == XmlNodeType.Element)
                {
                    if (sub.LocalName == "v")
                    {
                        value = sub.ReadElementContentAsString();
                        continue;
                    }

                    if (sub.LocalName == "t" && hasInline && !phonetic)
                    {
                        inline.Append(sub.ReadElementContentAsString());
                        continue;
                    }

                    if (sub.LocalName == "f")
                    {
                        cell.HasFormula = true;
                    }
                    else if (sub.LocalName == "is")
                    {
                        hasInline = true;
                    }
                    else if (sub.LocalName == "rPh" && !sub.IsEmptyElement)
                    {
                        phonetic = true;
                    }
                }
                else if (sub.NodeType == XmlNodeType.EndElement && sub.LocalName == "rPh")
                {
                    phonetic = false;
                }

                sub.Read();
            }
        }

        cell.Text = cell.Type switch
        {
            "inlineStr" => inline.ToString(),
            "s" => value is null ? null : SharedString(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => value,
        };
        return cell;
    }

    private string SharedString(int index)
    {
        if (_sharedStrings is null)
        {
            _sharedStrings = [];
            if (SharedStringsPath is not null)
            {
                using var xml = Open(SharedStringsPath);
                StringBuilder? item = null;
                var phonetic = false;
                xml.Read();
                while (!xml.EOF)
                {
                    if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t" && item is not null && !phonetic)
                    {
                        item.Append(xml.ReadElementContentAsString());
                        continue;
                    }

                    if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "si")
                    {
                        item = new StringBuilder();
                        if (xml.IsEmptyElement)
                        {
                            _sharedStrings.Add(string.Empty);
                            item = null;
                        }
                    }
                    else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "rPh")
                    {
                        phonetic = !xml.IsEmptyElement;
                    }
                    else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "rPh")
                    {
                        phonetic = false;
                    }
                    else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "si" && item is not null)
                    {
                        _sharedStrings.Add(item.ToString());
                        item = null;
                    }

                    xml.Read();
                }
            }
        }

        return index < _sharedStrings.Count ? _sharedStrings[index] : $"<missing shared string {index}>";
    }

    private ZipArchiveEntry? Entry(string path) => _zip.GetEntry(path.TrimStart('/'));

    private XmlReader Open(string path)
    {
        var entry = Entry(path) ?? throw new InvalidDataException($"Part '{path}' is missing.");
        return XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = true,
        });
    }

    private IEnumerable<(string Id, string Type, string Target)> RelationshipTargets(string relationshipsPath, string baseFolder)
    {
        if (Entry(relationshipsPath) is null)
        {
            yield break;
        }

        using var xml = Open(relationshipsPath);
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship")
            {
                var target = xml.GetAttribute("Target") ?? string.Empty;
                target = target.StartsWith('/') ? target.TrimStart('/') : baseFolder + target;
                yield return (xml.GetAttribute("Id") ?? string.Empty, xml.GetAttribute("Type") ?? string.Empty, target);
            }
        }
    }

    private static int CountChildren(XmlReader xml, string localName)
    {
        var depth = xml.Depth;
        var count = 0;
        while (xml.Read() && xml.Depth > depth)
        {
            if (xml.NodeType == XmlNodeType.Element && xml.Depth == depth + 1 && xml.LocalName == localName)
            {
                count++;
            }
        }

        return count;
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z')
            {
                break;
            }

            index = (index * 26) + (ch - 'A' + 1);
        }

        return index - 1;
    }
}
