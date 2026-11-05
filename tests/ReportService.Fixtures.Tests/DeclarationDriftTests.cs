using System.Text.Json;
using System.Text.RegularExpressions;
using ReportService.PdfOracle;
using ReportService.Testing;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// Every declaration the judge can be handed, held to the package it declares: each of its
/// columns is the record member the row part prints as <c>item.&lt;member&gt;</c>, its
/// captions are the ones the document start prints, its margins are the manifest's, and its
/// header members are the ones the document header prints. For the sample template the
/// check goes one step further: its records carry every column the declaration names. A
/// declaration that drifts from its package fails here, not in a run.
/// </summary>
public sealed class DeclarationDriftTests
{
    /// <summary>Every declaration and the package it declares, by the package's id.</summary>
    public static TheoryData<string, string> Declarations { get; } = new()
    {
        { "integrity-table", Path.Combine("tests", "ReportService.Testing", "Templates", "integrity-table") },
        { "integrity-grid", Path.Combine("tests", "ReportService.Testing", "Templates", "integrity-grid") },
        { "pilot-simple", Path.Combine("samples", "templates", "pilot-simple") },
    };

    [Theory]
    [MemberData(nameof(Declarations))]
    public void EveryDeclaredColumnIsTheMemberItsRowPartPrints(string id, string packagePath)
    {
        var expectation = Load(id);
        var row = File.ReadAllText(Path.Combine(RepositoryPaths.Root, packagePath, "html", "row.html.sbn"));

        var members = Regex.Matches(row, @"item\.(?<member>[A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups["member"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            string.Join(", ", members),
            string.Join(", ", expectation.Columns.Select(column => column.Column)));
    }

    [Theory]
    [MemberData(nameof(Declarations))]
    public void EveryDeclarationHoldsItsPackagesMarginsAndCaptions(string id, string packagePath)
    {
        var expectation = Load(id);
        var root = Path.Combine(RepositoryPaths.Root, packagePath);
        var manifest = File.ReadAllText(Path.Combine(root, "template.yaml"));
        var start = File.ReadAllText(Path.Combine(root, "html", "document-start.html.sbn"));

        Assert.Contains(
            $"marginMm: {{ top: {Number(expectation.TopMm)}, right: {Number(expectation.RightMm)}, bottom: {Number(expectation.BottomMm)}, left: {Number(expectation.LeftMm)} }}",
            manifest,
            StringComparison.Ordinal);

        foreach (var caption in expectation.HeadRow)
        {
            Assert.Contains($">{caption}<", start, StringComparison.Ordinal);
        }

        foreach (var anchor in expectation.HeaderAnchors)
        {
            Assert.Contains(anchor.Replace("$.", "header.", StringComparison.Ordinal), start, StringComparison.Ordinal);
        }

        Assert.Contains(expectation.RowKey, expectation.Columns.Select(column => column.Column));
        Assert.Contains(expectation.AmountColumn, expectation.Columns.Select(column => column.Column));
    }

    [Fact]
    public void TheSamplesRecordsCarryEveryColumnItsDeclarationNames()
    {
        var expectation = Load("pilot-simple");
        var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", "pilot-simple", "sample.json")));

        var items = sample.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            foreach (var column in expectation.Columns)
            {
                Assert.True(
                    item.TryGetProperty(column.Column, out _),
                    $"a record of the sample holds no '{column.Column}', the member its declaration's column prints.");
            }
        }
    }

    private static ExpectationDocument Load(string id) =>
        ExpectationDocument.Load(Path.Combine(
            RepositoryPaths.Root, "tests", "ReportService.Testing", "OracleExpectations", id + ".json"));

    private static string Number(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
