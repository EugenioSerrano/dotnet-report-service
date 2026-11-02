using System.Text.Json.Nodes;
using ReportService.Testing;
using ReportService.Testing.Fixtures;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The fixture library: a snapshot per core fixture, generated from the pilot
/// dataset after its hash is checked, identical on every run.
/// </summary>
public sealed class FixtureLibraryTests
{
    private static readonly Lazy<FixtureGenerator> Shared = new(() =>
        new FixtureGenerator(FixtureDataset.Load(DatasetPath, SumsPath)));

    private static FixtureGenerator Generator => Shared.Value;

    private static string DatasetDirectory => Path.Combine(RepositoryPaths.Root, "samples", "datasets");

    private static string DatasetPath => Path.Combine(DatasetDirectory, "header-items-v1.grouped.10k.json");

    private static string SumsPath => Path.Combine(DatasetDirectory, "SHA256SUMS");

    internal static JsonObject Root(SnapshotFixture fixture) =>
        JsonNode.Parse(fixture.Bytes) as JsonObject
        ?? throw new InvalidOperationException($"{fixture.Id} is not a JSON object.");

    internal static JsonObject Item(SnapshotFixture fixture, int sequence) =>
        Root(fixture)["items"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => FixtureGenerator.Sequence(item) == sequence);

    internal static JsonObject Header(SnapshotFixture fixture) => Root(fixture)["header"]!.AsObject();

    [Fact]
    public void EveryFixtureIsAFunctionOfTheDatasetAndItsId()
    {
        foreach (var id in FixtureGenerator.Ids)
        {
            var first = Generator.Build(id);
            var second = Generator.Build(id);

            Assert.Equal(first.Sha256, second.Sha256);
            Assert.Equal(first.Bytes, second.Bytes);
            Assert.Equal(first.RecordCount, second.RecordCount);
        }
    }

    [Fact]
    public void TheLibraryIsTheNamedCoreSet()
    {
        Assert.Equal(11, FixtureGenerator.Ids.Count);
        Assert.Equal(
            [
                "A1-esc", "B100", "B1000", "F26K-a", "GE30", "K1",
                "K0", "T300u", "X256K", "X256K-one-over", "X64K",
            ],
            FixtureGenerator.Ids);
    }

    [Fact]
    public void ADatasetThatDoesNotMatchItsSumIsRefused()
    {
        var folder = Directory.CreateTempSubdirectory("report-fixtures-");
        try
        {
            var dataset = Path.Combine(folder.FullName, "dataset.json");
            File.Copy(DatasetPath, dataset);
            var sums = Path.Combine(folder.FullName, "SHA256SUMS");
            File.WriteAllText(sums, new string('0', 64) + "  dataset.json\n");

            var refused = Assert.Throws<InvalidDataException>(() => FixtureDataset.Load(dataset, sums));

            Assert.Contains("dataset.json", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ADatasetWithoutItsSumsLineIsRefused()
    {
        var folder = Directory.CreateTempSubdirectory("report-fixtures-");
        try
        {
            var dataset = Path.Combine(folder.FullName, "dataset.json");
            File.Copy(DatasetPath, dataset);
            var sums = Path.Combine(folder.FullName, "SHA256SUMS");
            File.WriteAllText(sums, string.Empty);

            Assert.Throws<InvalidDataException>(() => FixtureDataset.Load(dataset, sums));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("A1-esc", 3)]
    [InlineData("B100", 100)]
    [InlineData("B1000", 1000)]
    [InlineData("F26K-a", 20)]
    [InlineData("GE30", 250)]
    [InlineData("K1", 1)]
    [InlineData("K0", 0)]
    [InlineData("T300u", 100)]
    [InlineData("X256K", 100)]
    [InlineData("X256K-one-over", 100)]
    [InlineData("X64K", 100)]
    public void TheSnapshotHoldsTheRecordsItDeclares(string id, int records)
    {
        var fixture = Generator.Build(id);

        Assert.Equal(records, fixture.RecordCount);
        Assert.Equal(records, Root(fixture)["items"]!.AsArray().Count);
        Assert.Equal("1.0", Root(fixture)["contractVersion"]!.GetValue<string>());
    }

    [Fact]
    public void TheBlockOfOneHundredRecordsStartsAtTheFirstRecordInOrder()
    {
        var items = Root(Generator.Build("B100"))["items"]!.AsArray()
            .Select(item => FixtureGenerator.Sequence(item!.AsObject()))
            .ToList();

        Assert.Equal(Enumerable.Range(1, 100), items);
    }

    [Fact]
    public void TheCalibrationFixtureHoldsTheLongestDescriptionOfTheFirstThousand()
    {
        var fixture = Generator.Build("K1");
        var records = Root(fixture)["items"]!.AsArray();
        var dataset = FixtureDataset.Load(DatasetPath, SumsPath);

        var longest = dataset.Items.Take(1000)
            .Select(item => item["description"]!.GetValue<string>().Length)
            .Max();

        Assert.Single(records);
        Assert.Equal(longest, records[0]!["description"]!.GetValue<string>().Length);
    }

    [Fact]
    public void TheTallRowHoldsTheMeasuredAnchorLength()
    {
        var description = Item(Generator.Build("T300u"), FixtureGenerator.TallRowSequence)["description"]!.GetValue<string>();

        Assert.Equal(FixtureGenerator.TallRowLength, description.Length);
        Assert.NotEqual(' ', description[^1]);
    }

    [Theory]
    [InlineData("X64K", 65_536)]
    [InlineData("X256K", 262_144)]
    public void ARecordSizedFixtureStaysInsideItsBracket(string id, int maximum)
    {
        var item = Item(Generator.Build(id), FixtureGenerator.TallRowSequence);
        var size = FixtureGenerator.CompactItemBytes(item);

        Assert.InRange(size, maximum - 64, maximum);
    }

    [Fact]
    public void TheRecordOneByteOverTheBoundIsExactlyOneByteOver()
    {
        var item = Item(Generator.Build("X256K-one-over"), FixtureGenerator.TallRowSequence);

        Assert.Equal(FixtureGenerator.RowBoundBytes + 1, FixtureGenerator.CompactItemBytes(item));
    }

    [Fact]
    public void TheEscapingFixtureHoldsTheSpecialCharacters()
    {
        var description = Item(Generator.Build("A1-esc"), 1)["description"]!.GetValue<string>();

        Assert.Contains('<', description);
        Assert.Contains('&', description);
        Assert.Contains('"', description);
        Assert.Contains('\u00A0', description);
        Assert.Contains('\u00E9', description);
        Assert.Contains('\u00FF', description);
    }

    [Fact]
    public void TheSoftHyphenIsNotInTheEscapingFixture()
    {
        var description = Item(Generator.Build("A1-esc"), 1)["description"]!.GetValue<string>();

        // A character the browser does not paint would make the fixture
        // impossible to verify, so it is left out of the escaping text.
        Assert.DoesNotContain('\u00AD', description);
    }

    [Fact]
    public void TheEmptySnapshotCarriesAHeaderAndNoRecord()
    {
        var fixture = Generator.Build("K0");
        var header = Header(fixture);

        Assert.Empty(Root(fixture)["items"]!.AsArray());
        Assert.False(string.IsNullOrWhiteSpace(header["reportTitle"]!.GetValue<string>()));
        Assert.Equal(0, header["totals"]!["recordCount"]!.GetValue<int>());
    }

    [Fact]
    public void TheFreeTextFixtureCarriesItsMeasuredText()
    {
        var freeText = Header(Generator.Build("F26K-a"))["freeText"]!.GetValue<string>();

        Assert.Equal(FixtureGenerator.FreeTextLength, freeText.Length);
    }

    [Fact]
    public void TheGroupEdgeFixtureHoldsThirtyLinesOnItsFirstRow()
    {
        var fixture = Generator.Build("GE30");
        var description = Item(fixture, FixtureGenerator.GroupEdgeFirstSequence)["description"]!.GetValue<string>();
        var lines = description.Split('\n');

        Assert.Equal(FixtureGenerator.GroupEdgeLineCount, lines.Length);
        Assert.Equal("edgeline01", lines[0]);
        Assert.Equal("edgeline30", lines[^1]);
    }

    [Fact]
    public void NoFixtureHoldsAMarkASoftHyphenOrAControlCharacter()
    {
        foreach (var fixture in Generator.BuildAll())
        {
            var text = fixture.Text;

            Assert.DoesNotContain("(continues)", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\u00AD', text);
            foreach (var value in text)
            {
                Assert.False(char.IsControl(value) && value != '\n', $"{fixture.Id} holds U+{(int)value:X4}.");
            }
        }
    }

    [Fact]
    public void TheFolderHoldsEveryFixtureAndItsSum()
    {
        var folder = Directory.CreateTempSubdirectory("report-fixtures-");
        try
        {
            Generator.WriteFolder(folder.FullName);

            var sums = File.ReadAllLines(Path.Combine(folder.FullName, FixtureGenerator.SumsFileName));
            Assert.Equal(FixtureGenerator.Ids.Count, sums.Length);
            Assert.Equal(
                FixtureGenerator.Ids.Select(id => id + ".json"),
                sums.Select(line => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..]));

            foreach (var id in FixtureGenerator.Ids)
            {
                var path = Path.Combine(folder.FullName, id + ".json");
                Assert.True(File.Exists(path), $"{id} was not written.");
                Assert.Equal(Generator.Build(id).Sha256, FixtureDataset.HashFile(path));
            }

            Assert.Equal(string.Join('\n', sums) + "\n", File.ReadAllText(Path.Combine(folder.FullName, FixtureGenerator.SumsFileName)));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
