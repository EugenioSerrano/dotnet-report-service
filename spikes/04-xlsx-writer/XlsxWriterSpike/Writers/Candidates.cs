using System.IO.Compression;
using System.IO.Packaging;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MiniExcelLibs;
using MiniExcelLibs.OpenXml;
using XlsxWriterSpike.Typing;

namespace XlsxWriterSpike.Writers;

/// <summary>One way of writing the typed rows to an XLSX file.</summary>
internal interface ISheetCandidate
{
    /// <summary>Command-line name.</summary>
    string Name { get; }

    /// <summary>Writes every sheet of the plan to <paramref name="outputPath"/>.</summary>
    void Write(RecordSource source, SheetPlan plan, string outputPath);
}

/// <summary>The candidates by command-line name.</summary>
internal static class Candidates
{
    /// <summary>Every candidate name, writing candidates first.</summary>
    public static readonly string[] Names = ["miniexcel", "openxml-default", "openxml-writeonly", "openxml-zip", "read-only"];

    /// <summary>Candidates that produce a file.</summary>
    public static readonly string[] WritingNames = ["miniexcel", "openxml-default", "openxml-writeonly", "openxml-zip"];

    /// <summary>Creates a candidate by name.</summary>
    public static ISheetCandidate Create(string name) => name switch
    {
        "miniexcel" => new MiniExcelCandidate(),
        "openxml-default" => new OpenXmlDefaultCandidate(),
        "openxml-writeonly" => new OpenXmlWriteOnlyCandidate(),
        "openxml-zip" => new OpenXmlZipCandidate(),
        "read-only" => new ReadOnlyCandidate(),
        _ => throw new UsageException($"Unknown candidate '{name}' ({string.Join(", ", Names)})."),
    };

    /// <summary>Opens the output file; it must not exist yet.</summary>
    public static FileStream CreateOutput(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 65_536);
}

/// <summary>MiniExcel 1.46.0 with its default configuration, fed a lazy sequence of dictionaries.</summary>
internal sealed class MiniExcelCandidate : ISheetCandidate
{
    /// <inheritdoc />
    public string Name => "miniexcel";

    /// <inheritdoc />
    public void Write(RecordSource source, SheetPlan plan, string outputPath)
    {
        object value;
        if (plan.Sheets.Count == 1)
        {
            value = Dictionaries(source, plan.Sheets[0], plan.RowLimit);
        }
        else
        {
            var sheets = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var window in plan.Sheets)
            {
                sheets[window.Name] = Dictionaries(source, window, plan.RowLimit);
            }

            value = sheets;
        }

        using var stream = Candidates.CreateOutput(outputPath);
        MiniExcel.SaveAs(
            stream,
            value: value,
            printHeader: true,
            sheetName: plan.Sheets[0].Name,
            excelType: ExcelType.XLSX,
            configuration: new OpenXmlConfiguration());
    }

    private static IEnumerable<IDictionary<string, object?>> Dictionaries(RecordSource source, SheetWindow window, long rowLimit)
    {
        var columns = source.Profile.Columns;
        foreach (var row in source.Rows(window, rowLimit))
        {
            var dictionary = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                var cell = row.Cells[i];
                dictionary[columns[i].Name] = cell.Kind switch
                {
                    CellKind.Number => cell.Number,
                    CellKind.Text => cell.Text,
                    _ => null,
                };
            }

            yield return dictionary;
        }
    }
}

/// <summary>Shared Open XML SDK package writing: worksheets, then styles, then workbook, each once with <see cref="OpenXmlWriter"/>.</summary>
internal static class OpenXmlPackageWriting
{
    /// <summary>Writes every part of the plan into <paramref name="document"/>.</summary>
    public static void WriteParts(SpreadsheetDocument document, RecordSource source, SheetPlan plan)
    {
        var workbookPart = document.AddWorkbookPart();
        var sheets = new List<(string Name, string RelationshipId)>();
        foreach (var window in plan.Sheets)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            sheets.Add((window.Name, workbookPart.GetIdOfPart(worksheetPart)));
            using var writer = OpenXmlWriter.Create(worksheetPart);
            WorksheetMarkup.WriteWorksheet(writer, source.Profile, source.Rows(window, plan.RowLimit));
        }

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        using (var writer = OpenXmlWriter.Create(stylesPart))
        {
            writer.WriteStartDocument();
            writer.WriteElement(WorksheetMarkup.CreateStylesheet());
        }

        using (var writer = OpenXmlWriter.Create(workbookPart))
        {
            writer.WriteStartDocument();
            writer.WriteElement(WorksheetMarkup.CreateWorkbook(sheets));
        }
    }
}

/// <summary>The SDK's usual path: <c>SpreadsheetDocument.Create(path)</c>.</summary>
internal sealed class OpenXmlDefaultCandidate : ISheetCandidate
{
    /// <inheritdoc />
    public string Name => "openxml-default";

    /// <inheritdoc />
    public void Write(RecordSource source, SheetPlan plan, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            throw new SpikeException("The output file already exists.");
        }

        using var document = SpreadsheetDocument.Create(outputPath, SpreadsheetDocumentType.Workbook);
        OpenXmlPackageWriting.WriteParts(document, source, plan);
    }
}

/// <summary>The SDK on a write-only <see cref="Package"/>, so the package layer can stream.</summary>
internal sealed class OpenXmlWriteOnlyCandidate : ISheetCandidate
{
    /// <inheritdoc />
    public string Name => "openxml-writeonly";

    /// <inheritdoc />
    public void Write(RecordSource source, SheetPlan plan, string outputPath)
    {
        using var stream = Candidates.CreateOutput(outputPath);
        using var package = Package.Open(stream, FileMode.Create, FileAccess.Write);
        using var document = SpreadsheetDocument.Create(package, SpreadsheetDocumentType.Workbook);
        OpenXmlPackageWriting.WriteParts(document, source, plan);
    }
}

/// <summary>
/// The SDK writer without the SDK package layer: a BCL <see cref="ZipArchive"/>
/// with the package parts written by this code and the SpreadsheetML parts by
/// <see cref="OpenXmlWriter"/>.
/// </summary>
internal sealed class OpenXmlZipCandidate : ISheetCandidate
{
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocumentRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <inheritdoc />
    public string Name => "openxml-zip";

    /// <inheritdoc />
    public void Write(RecordSource source, SheetPlan plan, string outputPath)
    {
        using var stream = Candidates.CreateOutput(outputPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var count = plan.Sheets.Count;

        WriteXml(zip, "[Content_Types].xml", xml =>
        {
            xml.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            Default(xml, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(xml, "xml", "application/xml");
            Override(xml, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            for (var i = 1; i <= count; i++)
            {
                Override(xml, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            }

            Override(xml, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            xml.WriteEndElement();
        });

        WriteXml(zip, "_rels/.rels", xml =>
        {
            xml.WriteStartElement("Relationships", RelationshipsNamespace);
            Relationship(xml, "rId1", DocumentRelationships + "/officeDocument", "xl/workbook.xml");
            xml.WriteEndElement();
        });

        WriteXml(zip, "xl/_rels/workbook.xml.rels", xml =>
        {
            xml.WriteStartElement("Relationships", RelationshipsNamespace);
            for (var i = 1; i <= count; i++)
            {
                Relationship(xml, $"rId{i}", DocumentRelationships + "/worksheet", $"worksheets/sheet{i}.xml");
            }

            Relationship(xml, $"rId{count + 1}", DocumentRelationships + "/styles", "styles.xml");
            xml.WriteEndElement();
        });

        var sheets = new List<(string Name, string RelationshipId)>();
        for (var i = 0; i < count; i++)
        {
            var window = plan.Sheets[i];
            sheets.Add((window.Name, $"rId{i + 1}"));
            WriteSpreadsheetMl(zip, $"xl/worksheets/sheet{i + 1}.xml",
                writer => WorksheetMarkup.WriteWorksheet(writer, source.Profile, source.Rows(window, plan.RowLimit)));
        }

        WriteSpreadsheetMl(zip, "xl/styles.xml", writer =>
        {
            writer.WriteStartDocument();
            writer.WriteElement(WorksheetMarkup.CreateStylesheet());
        });

        WriteSpreadsheetMl(zip, "xl/workbook.xml", writer =>
        {
            writer.WriteStartDocument();
            writer.WriteElement(WorksheetMarkup.CreateWorkbook(sheets));
        });
    }

    private static void WriteXml(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var xml = XmlWriter.Create(entry, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
        xml.WriteStartDocument(true);
        write(xml);
        xml.WriteEndDocument();
    }

    private static void WriteSpreadsheetMl(ZipArchive zip, string name, Action<OpenXmlWriter> write)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = OpenXmlWriter.Create(entry);
        write(writer);
    }

    private static void Default(XmlWriter xml, string extension, string contentType)
    {
        xml.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types");
        xml.WriteAttributeString("Extension", extension);
        xml.WriteAttributeString("ContentType", contentType);
        xml.WriteEndElement();
    }

    private static void Override(XmlWriter xml, string partName, string contentType)
    {
        xml.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types");
        xml.WriteAttributeString("PartName", partName);
        xml.WriteAttributeString("ContentType", contentType);
        xml.WriteEndElement();
    }

    private static void Relationship(XmlWriter xml, string id, string type, string target)
    {
        xml.WriteStartElement("Relationship", RelationshipsNamespace);
        xml.WriteAttributeString("Id", id);
        xml.WriteAttributeString("Type", type);
        xml.WriteAttributeString("Target", target);
        xml.WriteEndElement();
    }
}

/// <summary>Reads and types every record and writes nothing: the reader's own cost.</summary>
internal sealed class ReadOnlyCandidate : ISheetCandidate
{
    /// <inheritdoc />
    public string Name => "read-only";

    /// <inheritdoc />
    public void Write(RecordSource source, SheetPlan plan, string outputPath)
    {
        long cells = 0;
        foreach (var window in plan.Sheets)
        {
            foreach (var row in source.Rows(window, plan.RowLimit))
            {
                cells += row.Cells.Length;
            }
        }

        GC.KeepAlive(cells);
    }
}
