using System.Xml.Linq;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The role separation of the readers: the product's PDF reader is referenced by one
/// product project only, and the judge that decides a document independently
/// references no product assembly at all. Both are read from the project files, so
/// the rule is checked where a reference is declared, not where it happens to load.
/// </summary>
public sealed class ReaderReferenceTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void ThePdfReaderIsReferencedByTheVerificationProjectOnly()
    {
        var referencing = ProjectFiles("src")
            .Where(file => PackageReferences(file).Any(reference => reference == "PdfPig"))
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["ReportService.Pdf.Verification"], referencing);
    }

    [Fact]
    public void TheJudgeReferencesNoProductAssembly()
    {
        var judge = Path.Combine(Root, "tests", "ReportService.PdfOracle", "ReportService.PdfOracle.csproj");
        Assert.True(File.Exists(judge), judge);

        var references = XDocument.Load(judge).Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(references, reference => reference.Contains("ReportService.Pdf.Verification", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference.Contains("ReportService.Rendering", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference.Contains("ReportService.Templates", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ProjectFiles(string folder) =>
        Directory.EnumerateFiles(Path.Combine(Root, folder), "*.csproj", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> PackageReferences(string project) =>
        XDocument.Load(project).Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dotnet-report-service.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
