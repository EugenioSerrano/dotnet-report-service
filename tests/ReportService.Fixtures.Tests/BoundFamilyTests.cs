using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReportService.Testing;
using ReportService.Testing.Fixtures;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The bound family: six snapshots whose record 2 carries a description of exactly
/// 262 144 decoded UTF-8 bytes, built by the explicit word-length rule, with the
/// over-bound twins one character larger. Every count is measured on the string.
/// </summary>
public sealed class BoundFamilyTests
{
    private static readonly Lazy<FixtureGenerator> Shared = new(() =>
        new FixtureGenerator(FixtureDataset.Load(DatasetPath, SumsPath)));

    private static FixtureGenerator Generator => Shared.Value;

    private static string DatasetDirectory => Path.Combine(RepositoryPaths.Root, "samples", "datasets");

    private static string DatasetPath => Path.Combine(DatasetDirectory, "header-items-v1.grouped.10k.json");

    private static string SumsPath => Path.Combine(DatasetDirectory, FixtureGenerator.SumsFileName);

    /// <summary>The six members, their decoded bytes, their characters and their letters.</summary>
    public static TheoryData<string, int, int, int> Members => new()
    {
        { BoundFamily.Ascii, 262_144, 262_144, 226_440 },
        { BoundFamily.TwoByte, 262_144, 148_924, 113_220 },
        { BoundFamily.ThreeByte, 262_144, 111_184, 75_480 },
        { BoundFamily.OverAscii, 262_145, 262_145, 226_441 },
        { BoundFamily.OverTwoByte, 262_146, 148_925, 113_221 },
        { BoundFamily.OverThreeByte, 262_147, 111_185, 75_481 },
    };

    [Theory]
    [MemberData(nameof(Members))]
    public void TheDescriptionHoldsTheExactBytesAndCharacters(string id, int bytes, int characters, int letters)
    {
        var description = BoundFamily.Description(id);

        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(description));
        Assert.Equal(characters, description.Length);
        Assert.Equal(letters, BoundFamily.Letters(id));
        Assert.Equal(BoundFamily.Spaces, description.Count(value => value == ' '));
        Assert.Equal(BoundFamily.Words, description.Split(' ').Length);
        Assert.False(description.StartsWith(' '));
        Assert.False(description.EndsWith(' '));
        Assert.DoesNotContain("  ", description, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', description);
    }

    [Fact]
    public void TheOneByteMemberAppliesTheWordLengthRule()
    {
        var words = BoundFamily.Description(BoundFamily.Ascii).Split(' ');

        Assert.Equal(35_705, words.Length);
        Assert.Equal(12_210, words.Count(word => word.Length == 7));
        Assert.Equal(23_495, words.Count(word => word.Length == 6));
        Assert.Equal(["service", "network", "balance", "journey", "freight"], words.Take(5));
        Assert.Equal("gasket", words[^1]);
        Assert.All(words, word => Assert.All(word, value => Assert.True(value < 128)));
    }

    [Fact]
    public void TheTwoByteMemberAppliesTheWordLengthRule()
    {
        var words = BoundFamily.Description(BoundFamily.TwoByte).Split(' ');

        Assert.Equal(35_705, words.Length);
        Assert.Equal(6_105, words.Count(word => word.Length == 4));
        Assert.Equal(29_600, words.Count(word => word.Length == 3));
        Assert.All(words, word => Assert.All(word, value => Assert.Equal('é', value)));
    }

    [Fact]
    public void TheThreeByteMemberAppliesTheWordLengthRule()
    {
        var words = BoundFamily.Description(BoundFamily.ThreeByte).Split(' ');

        Assert.Equal(35_705, words.Length);
        Assert.Equal(4_070, words.Count(word => word.Length == 3));
        Assert.Equal(31_635, words.Count(word => word.Length == 2));
        Assert.All(words, word => Assert.All(word, value => Assert.Equal('€', value)));
    }

    [Fact]
    public void TheOverBoundTwinAddsOneCharacterToItsMember()
    {
        Assert.Equal(BoundFamily.Description(BoundFamily.Ascii) + "a", BoundFamily.Description(BoundFamily.OverAscii));
        Assert.Equal(BoundFamily.Description(BoundFamily.TwoByte) + "é", BoundFamily.Description(BoundFamily.OverTwoByte));
        Assert.Equal(BoundFamily.Description(BoundFamily.ThreeByte) + "€", BoundFamily.Description(BoundFamily.OverThreeByte));
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void TheSourceHashIsTheExactSourceHash(string id, int bytes, int characters, int letters)
    {
        var description = BoundFamily.Description(id);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(description)));

        Assert.Equal(expected, BoundFamily.SourceSha256(id));
        Assert.Matches("^[0-9a-f]{64}$", BoundFamily.SourceSha256(id));
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(description));
        Assert.Equal(characters, description.Length);
        Assert.Equal(letters, BoundFamily.Letters(id));
    }

    [Theory]
    [InlineData(BoundFamily.Ascii)]
    [InlineData(BoundFamily.TwoByte)]
    [InlineData(BoundFamily.ThreeByte)]
    [InlineData(BoundFamily.OverAscii)]
    [InlineData(BoundFamily.OverTwoByte)]
    [InlineData(BoundFamily.OverThreeByte)]
    public void TheSnapshotIsPilotShapedAndHoldsTheBoundRecordSecond(string id)
    {
        var fixture = Generator.Build(id);
        var root = JsonNode.Parse(fixture.Bytes) as JsonObject
            ?? throw new InvalidOperationException($"{id} is not a JSON object.");

        Assert.Equal(3, fixture.RecordCount);
        Assert.Equal(["contractVersion", "header", "items"], root.Select(entry => entry.Key));
        Assert.Equal("1.0", root["contractVersion"]!.GetValue<string>());

        var header = root["header"] as JsonObject
            ?? throw new InvalidOperationException($"{id} holds no header.");
        Assert.Equal(
            ["datasetId", "reportTitle", "generatedAt", "seed", "currency", "customer", "totals"],
            header.Select(entry => entry.Key));
        Assert.Equal("pilot-simple", header["datasetId"]!.GetValue<string>());
        Assert.Equal(["id", "name"], (header["customer"] as JsonObject)!.Select(entry => entry.Key));
        Assert.Equal(3, header["totals"]!["recordCount"]!.GetValue<int>());
        Assert.Equal(3.00m, header["totals"]!["amount"]!.GetValue<decimal>());

        var items = root["items"] as JsonArray
            ?? throw new InvalidOperationException($"{id} holds no items.");
        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.Equal(
            ["sequence", "description", "quantity", "unitPrice", "amount"],
            (item as JsonObject)!.Select(entry => entry.Key)));
        Assert.Equal(BoundFamily.Description(id), items[1]!["description"]!.GetValue<string>());
        Assert.Equal("ordinary one", items[0]!["description"]!.GetValue<string>());
        Assert.Equal("ordinary three", items[2]!["description"]!.GetValue<string>());

        Assert.Equal(fixture.Sha256, Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes)));
        Assert.Contains(id, FixtureGenerator.Ids);
    }

    [Fact]
    public void TheFamilyIsTheSixNamedFixturesInOrdinalOrder()
    {
        Assert.Equal(
            ["boundAscii", "boundThreeByte", "boundTwoByte", "overAscii", "overThreeByte", "overTwoByte"],
            BoundFamily.Ids);
        Assert.All(BoundFamily.Ids, id => Assert.Contains(id, FixtureGenerator.Ids));
    }
}
