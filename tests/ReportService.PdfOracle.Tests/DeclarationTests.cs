using ReportService.Testing;

namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The declaration the judge reads beside the packages, held to what each package
/// itself declares: its manifest, its stylesheet and the classes its row template
/// prints. The columns are compared through the package's own widths and margins, so
/// a band the declaration states only in agreement with its own previous band is
/// caught, and the columns are proved to fill the content area without a gap or an
/// overlap.
/// </summary>
public sealed class DeclarationTests
{
    /// <summary>The width of an A4 page in millimetres, the paper both packages declare.</summary>
    private const double A4WidthMm = 210;

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void TheDeclarationHoldsThePageThePackageDeclares(string id)
    {
        var expectation = ExpectationDocument.Load(DeclarationPath(id));
        var manifest = File.ReadAllText(Path.Combine(PackagePath(id), "template.yaml"));

        Assert.Equal("A4", expectation.Paper);
        Assert.Contains("paper: A4", manifest, StringComparison.Ordinal);
        Assert.Contains("orientation: portrait", manifest, StringComparison.Ordinal);
        Assert.Equal(A4WidthMm, expectation.WidthMm);
        Assert.Equal(297, expectation.HeightMm);
        Assert.Contains(
            $"marginMm: {{ top: {Number(expectation.TopMm)}, right: {Number(expectation.RightMm)}, bottom: {Number(expectation.BottomMm)}, left: {Number(expectation.LeftMm)} }}",
            manifest,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void TheDeclarationHoldsTheColumnsAtTheOffsetThePackageGivesThem(string id)
    {
        var expectation = ExpectationDocument.Load(DeclarationPath(id));
        var package = PackageGeometry.Read(PackagePath(id));

        // The names are the record members the row template prints, in the order it prints
        // them: the bands are matched to the package's own widths by position, so a width is
        // never read against the wrong column.
        var row = File.ReadAllText(Path.Combine(PackagePath(id), "html", "row.html.sbn"));
        var members = System.Text.RegularExpressions.Regex
            .Matches(row, @"item\.(?<member>[A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups["member"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(string.Join(", ", members), string.Join(", ", expectation.Columns.Select(column => column.Column)));
        Assert.Equal(package.Columns.Count, expectation.Columns.Count);

        // Each band starts where the package's own widths put it, from its own left margin.
        for (var index = 0; index < package.Columns.Count; index++)
        {
            Assert.Equal(package.Bands[index].StartMm, expectation.Columns[index].StartMm, 3);
            Assert.Equal(package.Bands[index].EndMm, expectation.Columns[index].EndMm, 3);
        }

        // The columns fill the container and the container fills the page between the
        // margins: the columns touch without a gap and without an overlap, and the last
        // one ends at the right margin — proved from the package, not from the declaration.
        Assert.Equal(package.ContainerWidthMm, package.WidthsMm.Sum(), 3);
        Assert.Equal(A4WidthMm - package.LeftMarginMm - package.RightMarginMm, package.ContainerWidthMm, 3);
        Assert.Equal(package.LeftMarginMm, expectation.Columns[0].StartMm, 3);
        Assert.Equal(A4WidthMm - package.RightMarginMm, expectation.Columns[^1].EndMm, 3);
    }

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void TheDeclaredRolesNameColumnsTheDeclarationHolds(string id)
    {
        var expectation = ExpectationDocument.Load(DeclarationPath(id));
        var columns = expectation.Columns.Select(column => column.Column).ToList();

        Assert.Contains(expectation.RowKey, columns);
        Assert.Contains(expectation.AmountColumn, columns);
        Assert.Equal("sequence", expectation.RowKey);
        Assert.Equal("amount", expectation.AmountColumn);
    }

    [Theory]
    [InlineData("integrity-table")]
    [InlineData("integrity-grid")]
    public void TheDeclarationHoldsTheCaptionsAndHeaderValuesThePackagePrints(string id)
    {
        var expectation = ExpectationDocument.Load(DeclarationPath(id));
        var start = File.ReadAllText(Path.Combine(PackagePath(id), "html", "document-start.html.sbn"));

        foreach (var caption in expectation.HeadRow)
        {
            Assert.Contains($">{caption}<", start, StringComparison.Ordinal);
        }

        foreach (var anchor in expectation.HeaderAnchors)
        {
            Assert.Contains(anchor.Replace("$.", "header.", StringComparison.Ordinal), start, StringComparison.Ordinal);
        }

        Assert.Contains(expectation.Trail!.Replace("$.", "header.", StringComparison.Ordinal), File.ReadAllText(Path.Combine(PackagePath(id), "html", "document-end.html.sbn")), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclarationWithoutAColumnIsRefused()
    {
        var text = """
            {
              "paper": "A4",
              "widthMm": 210,
              "heightMm": 297,
              "marginMm": { "top": 15, "right": 12, "bottom": 18, "left": 12 },
              "columns": [],
              "headRow": ["Seq"],
              "headerAnchors": ["$.reportTitle"],
              "grouped": false,
              "blockTotal": false
            }
            """;

        Assert.Throws<InvalidDataException>(() => ExpectationDocument.Parse(text));
    }

    [Fact]
    public void ADeclarationWithoutItsRolesIsRefused()
    {
        // The judge never guesses which column identifies a record or which one must print
        // on the row's first page: a declaration that omits either role is refused.
        var text = """
            {
              "paper": "A4",
              "widthMm": 210,
              "heightMm": 297,
              "marginMm": { "top": 15, "right": 12, "bottom": 18, "left": 12 },
              "columns": [ { "column": "sequence", "startMm": 12, "endMm": 26 } ],
              "headRow": ["Seq"],
              "headerAnchors": ["$.reportTitle"],
              "grouped": false,
              "blockTotal": false
            }
            """;

        var withoutRoles = Assert.Throws<InvalidDataException>(() => ExpectationDocument.Parse(text));
        Assert.Contains("rowKey", withoutRoles.Message, StringComparison.Ordinal);

        var unknownRole = Assert.Throws<InvalidDataException>(() => ExpectationDocument.Parse(
            text.Replace("\"grouped\"", "\"rowKey\": \"sequence\", \"amountColumn\": \"total\", \"grouped\"", StringComparison.Ordinal)));
        Assert.Contains("amountColumn", unknownRole.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclarationThatIsNotAnObjectIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => ExpectationDocument.Parse("[]"));
    }

    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string PackagePath(string id) =>
        Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates", id);

    private static string DeclarationPath(string id) =>
        Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "OracleExpectations", id + ".json");
}
