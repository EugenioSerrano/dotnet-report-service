using System.Text.Json;
using System.Text.RegularExpressions;
using ReportService.Testing;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The page frame's four geometries: each declaration's paper, orientation and
/// margins are the ones its judge declares, the four differ in nothing else, and the
/// page elements and column headers the judge reads are the words the package prints.
/// </summary>
public sealed partial class PageFramePackageTests
{
    private static readonly (string Declaration, string Expectation)[] Geometries =
    [
        ("template.yaml", "page-frame-a4-portrait"),
        ("manifests/a4-landscape.yaml", "page-frame-a4-landscape"),
        ("manifests/letter-portrait.yaml", "page-frame-letter-portrait"),
        ("manifests/letter-landscape.yaml", "page-frame-letter-landscape"),
    ];

    private static string TemplatesDirectory => Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates");

    private static string ExpectationsDirectory => Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "OracleExpectations");

    private static string PackageFolder => Path.Combine(TemplatesDirectory, "page-frame");

    /// <summary>Every geometry, as the theory's two names.</summary>
    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (declaration, expectation) in Geometries)
            {
                data.Add(declaration, expectation);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheJudgeDeclaresThePageThePackageDeclares(string declaration, string expectation)
    {
        var manifest = File.ReadAllText(Path.Combine(PackageFolder, declaration));
        using var judge = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExpectationsDirectory, expectation + ".json")));
        var root = judge.RootElement;
        var margins = root.GetProperty("marginMm");

        Assert.Contains($"paper: {root.GetProperty("paper").GetString()}", manifest, StringComparison.Ordinal);
        Assert.Contains($"orientation: {OrientationOf(root)}", manifest, StringComparison.Ordinal);
        Assert.Contains(
            $"marginMm: {{ top: {Number(margins.GetProperty("top"))}, right: {Number(margins.GetProperty("right"))}, "
                + $"bottom: {Number(margins.GetProperty("bottom"))}, left: {Number(margins.GetProperty("left"))} }}",
            manifest,
            StringComparison.Ordinal);
        Assert.True(root.GetProperty("widthMm").GetDouble() > 0);
        Assert.True(root.GetProperty("heightMm").GetDouble() > 0);
    }

    [Fact]
    public void TheFourDeclarationsDifferOnlyInTheirPageOutput()
    {
        var bodies = Geometries
            .Select(geometry => PageOutput().Replace(File.ReadAllText(Path.Combine(PackageFolder, geometry.Declaration)), string.Empty))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Single(bodies);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ThePageElementsTheJudgeReadsAreTheOnesTheStylesheetPrints(string declaration, string expectation)
    {
        Assert.NotNull(declaration);
        var stylesheet = File.ReadAllText(Path.Combine(PackageFolder, "html", "page-frame.css"));
        using var judge = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExpectationsDirectory, expectation + ".json")));
        var root = judge.RootElement;

        Assert.Contains($"content: \"{root.GetProperty("pageHeader").GetString()}\"", stylesheet, StringComparison.Ordinal);
        Assert.Contains($"content: \"{root.GetProperty("pageFooter").GetString()}\"", stylesheet, StringComparison.Ordinal);
        Assert.Contains("counter(page)", stylesheet, StringComparison.Ordinal);
        Assert.Contains("counter(pages)", stylesheet, StringComparison.Ordinal);
        Assert.Equal("Hoja {X} de {Y}", root.GetProperty("pageNumbering").GetString());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheColumnHeadersTheJudgeReadsAreTheOnesThePackagePrints(string declaration, string expectation)
    {
        Assert.NotNull(declaration);
        var start = File.ReadAllText(Path.Combine(PackageFolder, "html", "document-start.html.sbn"));
        using var judge = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExpectationsDirectory, expectation + ".json")));

        foreach (var caption in judge.RootElement.GetProperty("headRow").EnumerateArray().Select(word => word.GetString()))
        {
            Assert.Contains(">" + caption + "<", start, StringComparison.Ordinal);
        }
    }

    private static string Number(JsonElement value) => value.GetRawText();

    private static string OrientationOf(JsonElement root) =>
        root.GetProperty("heightMm").GetDouble() > root.GetProperty("widthMm").GetDouble() ? "portrait" : "landscape";

    [GeneratedRegex(@"(?ms)^  pdf:.*?(?=^localeDefaults:)", RegexOptions.CultureInvariant)]
    private static partial Regex PageOutput();
}
