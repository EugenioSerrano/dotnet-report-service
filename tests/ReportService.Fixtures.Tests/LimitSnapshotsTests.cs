using System.Text.Json.Nodes;
using ReportService.Testing;
using ReportService.Testing.Limits;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The snapshots that sit at and beside the single block's two bounds: every family is the
/// document its name states - the record counts, and the byte families whose records occupy a
/// fixed number of bytes each - two generations of it are identical, and its SHA-256 is the one
/// recorded beside the type. A family that drifts changes a measurement nobody would notice
/// changing; these cases are what notice.
/// </summary>
public sealed class LimitSnapshotsTests
{
    private const long BlockRecordBytes = 1_048_576;

    [Fact]
    public void EveryFamilyIsIdenticalOnTwoGenerationsAndMatchesItsRecordedSum()
    {
        var families = LimitSnapshots.Families();
        var again = LimitSnapshots.Families();
        var sums = LimitSnapshots.ReadSums(SumsPath());

        foreach (var (name, document) in families)
        {
            Assert.Equal(document, again[name]);
            Assert.True(sums.TryGetValue(name, out var recorded), $"the sums file records no line for {name}");
            Assert.Equal(recorded, LimitSnapshots.Sha256(document));
        }

        Assert.Equal(families.Keys.Order(StringComparer.Ordinal), sums.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheRecordFamiliesHoldTheCountsTheirNamesState()
    {
        Assert.Equal(1_000, RecordCount(LimitSnapshots.RecordsAtBound()));
        Assert.Equal(1_001, RecordCount(LimitSnapshots.RecordsOverTheBound()));
        Assert.Equal(2_000, RecordCount(LimitSnapshots.RecordsAtTheMaximum()));
        Assert.Equal(2_001, RecordCount(LimitSnapshots.RecordsOverTheMaximum()));
        Assert.Equal(50_000, RecordCount(LimitSnapshots.RecordsBeyondBoth()));
    }

    [Fact]
    public void ThePrecedenceFamilyCarriesOneRecordOverThePerRecordBound()
    {
        var document = LimitSnapshots.RecordsWithOneOverTheRecordLimit();
        var records = LimitSnapshots.RecordBytes(document);

        Assert.Equal(1_001, records.Count);
        Assert.Equal(1_048_577, records[0]);
        Assert.DoesNotContain(records.Skip(1), bytes => bytes > 1_048_576);
    }

    [Fact]
    public void TheBytesAtTheBoundAreExactlyTheBlocksMaximum()
    {
        var records = LimitSnapshots.RecordBytes(LimitSnapshots.BytesAtTheBound());

        Assert.Equal(64, records.Count);
        Assert.All(records, bytes => Assert.Equal(16_384, bytes));
        Assert.Equal(BlockRecordBytes, records.Sum());
    }

    [Fact]
    public void TheFamilyOneByteOverTheBoundCrossesItAtItsLastRecord()
    {
        var records = LimitSnapshots.RecordBytes(LimitSnapshots.BytesOneOverTheBound());

        Assert.Equal(64, records.Count);
        Assert.Equal(BlockRecordBytes + 1, records.Sum());
        Assert.Equal(63 * 16_384, records.Take(63).Sum());
        Assert.Equal(16_385, records[^1]);
    }

    [Fact]
    public void TheSixHundredRecordFamilyCrossesTheByteBoundAtItsFiveHundredSeventyThirdRecord()
    {
        var records = LimitSnapshots.RecordBytes(LimitSnapshots.BytesCrossingAtRecord573());

        Assert.Equal(600, records.Count);
        Assert.All(records, bytes => Assert.Equal(1_833, bytes));
        Assert.True(records.Take(572).Sum() <= BlockRecordBytes, "the first 572 records fit the bound");
        Assert.True(records.Take(573).Sum() > BlockRecordBytes, "the 573rd record does not fit it");
        Assert.True(records.Take(572).Sum() + records[572] > BlockRecordBytes);
    }

    private static string SumsPath() =>
        Path.Combine(RepositoryPaths.Root, "tests", "Support", "ReportService.Testing", "Limits", LimitSnapshots.SumsFileName);

    private static int RecordCount(byte[] document)
    {
        var root = JsonNode.Parse(document)!.AsObject();
        return root["items"]!.AsArray().Count;
    }
}
