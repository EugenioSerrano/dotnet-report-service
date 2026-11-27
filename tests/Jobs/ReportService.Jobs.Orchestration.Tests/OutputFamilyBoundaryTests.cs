using System.Text.RegularExpressions;
using System.Xml.Linq;
using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// The layout by output family, as the source tree holds it: every part that produces an output
/// family's artifact lives under that family's one home, the job core references no family, the
/// families reference neither each other nor the host, and the identifiers that name a family and
/// stay in the core and shared projects are exactly the ones the layout keeps on purpose - one
/// more, or one fewer, fails here.
/// </summary>
public sealed partial class OutputFamilyBoundaryTests
{
    /// <summary>The types the PDF output family declares, all under <c>src/Pdf/</c>.</summary>
    private static readonly string[] PdfFamilyTypes =
    [
        "ReportPdfOutputWorkflow", "ReportPdfOutputInput", "PdfOutputWindow", "PdfOutputPartitionFacts",
        "PdfOutputPartitionFactsInput", "PdfOutputPartitionFactsResult", "PdfOutputStepContracts",
        "RenderPdfBlockStepInput", "RenderPdfBlockStepResult", "IRenderPdfBlockStep", "RenderPdfBlockStepOptions",
        "PdfOutputConcurrencyOptions", "AssemblePdfStepContracts", "AssemblePdfStepInput", "AssemblePdfStepResult", "IAssemblePdfStep", "AssemblePdfStepOptions",
        "PageCodeDeclarationInput", "PageCodeDeclarationResult", "PdfOutputActivities", "FindPdfOutputInput",
        "FindPdfOutputResult", "PublishOutputInput", "PdfPendingDispatchActivities", "ReadPendingDispatchInput",
        "PendingDispatch", "PendingDispatchMapper", "PageCodeDeclarationActivities", "PdfOutputFamily",
    ];

    /// <summary>The types the HTML output family declares, all under <c>src/Html/</c>.</summary>
    private static readonly string[] HtmlFamilyTypes =
    [
        "ReportHtmlOutputWorkflow", "ReportHtmlOutputInput", "AssembleHtmlActivity", "AssembleHtmlStepOptions",
        "HtmlOutputStepContracts", "AssembleHtmlStepInput", "AssembleHtmlStepResult", "IAssembleHtmlStep",
        "HtmlOutputActivities", "PublishHtmlOutputInput", "HtmlOutputFamily", "HtmlOutputAssembler",
        "HtmlAssetInliner", "HtmlAssemblyResult", "HtmlAssemblyOutcome", "HtmlRefusalCause", "HtmlRefusal",
        "HtmlInlineOutcome",
    ];

    /// <summary>
    /// The identifiers naming an output family that stay in the core and shared projects, file by
    /// file: the shared output vocabulary, the API's stage and wire values, the store's refusal
    /// columns, the recorded partition payload and the row bound of the one data pass, the data
    /// step's options and span tags, the identifier of a recorded patch, and the window decision
    /// the aging rule caps. Each is kept on purpose; see the repository layout.
    /// </summary>
    private static readonly Dictionary<string, string[]> Residuals = new(StringComparer.Ordinal)
    {
        ["Jobs/ReportService.Data/PartitionResult.cs"] = ["PdfOutputRefusal", "PdfRefusal"],
        ["Jobs/ReportService.Data/RowBound/PdfRowBound.cs"] = ["PdfRowBound"],
        ["Jobs/ReportService.Jobs/JobStages.cs"] = ["RenderingPdf"],
        ["Jobs/ReportService.Jobs/JobWireValues.cs"] = ["ComposingHtml", "Html", "MergingPdf", "Pdf", "RenderingPdf"],
        ["Jobs/ReportService.Jobs/OutputFormat.cs"] = ["Html", "Pdf"],
        ["Jobs/ReportService.Jobs/PartitionRecords.cs"] = ["PdfRefusalCode", "PdfRefusalReason"],
        ["Jobs/ReportService.Jobs/ReportJobStage.cs"] = ["ComposingHtml", "MergingPdf", "RenderingPdf"],
        ["Jobs/ReportService.Jobs/Store/PostgresJobStore.Rendering.cs"] = ["PdfRefusalCode", "PdfRefusalReason"],
        ["Jobs/ReportService.Jobs.Orchestration/Jobs/PdfOutputActivityOptions.cs"] = ["PdfOutputActivityOptions"],
        ["Jobs/ReportService.Jobs.Orchestration/Jobs/PdfOutputWindowDecision.cs"] = ["PdfOutputWindowAction", "PdfOutputWindowDecision"],
        ["Jobs/ReportService.Jobs.Orchestration/Jobs/PriorityAging.cs"] = ["PdfOutputWindowAction", "PdfOutputWindowDecision"],
        ["Jobs/ReportService.Jobs.Orchestration/Jobs/ReportJobWorkflow.cs"] = ["HtmlOutputPathPatch", "PdfOutputActivityOptions"],
        ["Jobs/ReportService.Jobs.Orchestration/Jobs/TemplatePartitionSpec.cs"] = ["Pdf"],
        ["Jobs/ReportService.Jobs.Orchestration/Jobs/ValidateAndPartitionActivity.cs"] =
        [
            "Pdf", "PdfOutputActivityOptions", "PdfOutputRefusal", "PdfOutputRefusedByRowBound", "PdfRefusalCode",
            "PdfRefusalCodeTag", "PdfRefusalOrdinalTag", "PdfRefusalReason", "PdfRowBound",
        ],
    };

    private static string Source => Path.Combine(RepositoryPaths.Root, "src");

    [Fact]
    public void EveryFamilyTypeIsDeclaredInItsFamilysHomeAndNowhereElse()
    {
        var declarations = SourceFiles("")
            .SelectMany(file => DeclaredTypes(file).Select(type => (Type: type, File: Relative(file))))
            .ToList();

        foreach (var (types, home) in new[] { (PdfFamilyTypes, "Pdf/"), (HtmlFamilyTypes, "Html/") })
        {
            foreach (var type in types)
            {
                var at = declarations.Where(declaration => declaration.Type == type).Select(declaration => declaration.File).ToList();
                Assert.True(at.Count > 0, $"{type} is declared nowhere under src/");
                Assert.All(at, file => Assert.True(file.StartsWith(home, StringComparison.Ordinal), $"{type} is declared in {file}, outside src/{home}"));
            }
        }
    }

    [Fact]
    public void TheCoreAndSharedProjectsNameAFamilyOnlyWhereTheLayoutKeepsItOnPurpose()
    {
        var found = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var file in SourceFiles("Jobs").Concat(SourceFiles("Platform")))
        {
            var text = Comments().Replace(File.ReadAllText(file), string.Empty);
            var names = FamilyIdentifier().Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (names.Length > 0)
            {
                found[Relative(file)] = names;
            }
        }

        var expected = new SortedDictionary<string, string[]>(
            Residuals.ToDictionary(pair => pair.Key, pair => pair.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal),
            StringComparer.Ordinal);
        Assert.Equal(expected.Keys, found.Keys);
        foreach (var (file, names) in expected)
        {
            Assert.True(names.SequenceEqual(found[file]), $"{file} names [{string.Join(", ", found[file])}], and the layout keeps [{string.Join(", ", names)}]");
        }
    }

    [Fact]
    public void TheCoreAndSharedProjectsReferenceNoFamilyAndNotTheHost()
    {
        foreach (var project in Projects("Jobs").Concat(Projects("Platform")))
        {
            foreach (var reference in References(project))
            {
                Assert.False(reference.StartsWith("Pdf/", StringComparison.Ordinal), $"{Relative(project)} references {reference}");
                Assert.False(reference.StartsWith("Html/", StringComparison.Ordinal), $"{Relative(project)} references {reference}");
                Assert.False(reference.StartsWith("ReportService.Workers.Orchestration/", StringComparison.Ordinal), $"{Relative(project)} references {reference}");
            }
        }
    }

    [Fact]
    public void TheFamiliesReferenceNeitherEachOtherNorTheHost()
    {
        foreach (var (family, other) in new[] { ("Pdf", "Html/"), ("Html", "Pdf/") })
        {
            foreach (var project in Projects(family))
            {
                foreach (var reference in References(project))
                {
                    Assert.False(reference.StartsWith(other, StringComparison.Ordinal), $"{Relative(project)} references {reference}");
                    Assert.False(reference.StartsWith("ReportService.Workers.Orchestration/", StringComparison.Ordinal), $"{Relative(project)} references {reference}");
                }
            }
        }
    }

    [Fact]
    public void TheCoreAssemblyLoadsNoFamilyAndNoWorker()
    {
        var shared = new[] { "ReportService.Templates", "ReportService.TemplatePackages", "ReportService.DataContract" };
        var referenced = typeof(ReportJobWorkflow).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToList();

        Assert.DoesNotContain(referenced, name => name.StartsWith("ReportService.Pdf.", StringComparison.Ordinal));
        Assert.DoesNotContain("ReportService.Html", referenced);
        Assert.DoesNotContain("ReportService.Rendering", referenced);
        Assert.DoesNotContain(referenced, name => name.StartsWith("ReportService.Workers.", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name.StartsWith("ReportService.Template", StringComparison.Ordinal) && !shared.Contains(name));
    }

    [Fact]
    public void TheOrchestrationHostReachesNoTemplateSourceAndNoRenderWorker()
    {
        var referenced = typeof(OrchestrationWorkerRegistration).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToList();

        Assert.DoesNotContain("ReportService.TemplateSource", referenced);
        Assert.DoesNotContain("ReportService.Workers.Pdf", referenced);
    }

    private static IEnumerable<string> SourceFiles(string under) =>
        Directory.EnumerateFiles(Path.Combine(Source, under), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file));

    private static IEnumerable<string> Projects(string under) =>
        Directory.EnumerateFiles(Path.Combine(Source, under), "*.csproj", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file));

    // Each project reference, as a path below src/ with forward slashes.
    private static IEnumerable<string> References(string project) =>
        XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(include => Relative(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include.Replace('\\', Path.DirectorySeparatorChar)))));

    private static IEnumerable<string> DeclaredTypes(string file) =>
        Declaration().Matches(Comments().Replace(File.ReadAllText(file), string.Empty)).Select(match => match.Groups["name"].Value);

    private static bool IsBuildOutput(string file)
    {
        var parts = Path.GetRelativePath(Source, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(Source, path).Replace(Path.DirectorySeparatorChar, '/');

    [GeneratedRegex(@"\b\w*(?:Pdf|Html)\w*\b")]
    private static partial Regex FamilyIdentifier();

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"^\s*(?:public|internal)\s+(?:(?:sealed|static|readonly|abstract|partial)\s+)*(?:class|record|enum|struct|interface)(?:\s+struct)?\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Multiline)]
    private static partial Regex Declaration();
}
