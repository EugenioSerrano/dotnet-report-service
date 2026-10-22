using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace XlsxWriterSpike.Inspection;

/// <summary>
/// Schema validation with the Open XML SDK validator. It loads the document
/// model, so it is used only on small files; large files are covered by the
/// streaming inspector and the Excel open check.
/// </summary>
internal static class PackageValidator
{
    /// <summary>Returns the number of validation errors and the first ten descriptions.</summary>
    public static (int Count, IReadOnlyList<string> First) Validate(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(document).ToList();
        var first = errors.Take(10)
            .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}".ReplaceLineEndings(" "))
            .ToList();
        return (errors.Count, first);
    }
}
