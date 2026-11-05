using ReportService.Testing.Fixtures;

namespace ReportService.RenderingTests;

/// <summary>
/// Two properties the verification depends on: the same document printed twice reads
/// the same, and a row that runs over a page break is read in the order it was
/// painted — the row's own text first, the next row's number after it. A contradiction
/// on the second property would mean the alignment the verification uses cannot hold
/// on the native path.
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class PrintedOrderTests(RenderingSuite suite)
{
    /// <summary>The fixtures whose content does not fit one page.</summary>
    public static TheoryData<string> LongFixtures => new() { { "B1000" }, { "T300u" } };

    /// <summary>Two prints of the same fixture read word for word the same.</summary>
    /// <param name="fixture">The fixture printed three times.</param>
    [Theory]
    [MemberData(nameof(LongFixtures))]
    public async Task TwoPrintsOfTheSameFixtureReadTheSame(string fixture)
    {
        var first = await suite.JudgeAsync(await suite.RenderAsync(fixture, "integrity-table", name: $"{fixture}-determinism-1"));
        var second = await suite.JudgeAsync(await suite.RenderAsync(fixture, "integrity-table", name: $"{fixture}-determinism-2"));
        var third = await suite.JudgeAsync(await suite.RenderAsync(fixture, "integrity-table", name: $"{fixture}-determinism-3"));

        Assert.True(first.Passed && second.Passed && third.Passed, $"{fixture}: {string.Join(", ", first.Classes)} {string.Join(", ", second.Classes)} {string.Join(", ", third.Classes)}");
        Assert.Equal(first.Pages, second.Pages);
        Assert.Equal(first.Pages, third.Pages);
        Assert.Equal(first.Reading, second.Reading);
        Assert.Equal(first.Reading, third.Reading);
    }

    /// <summary>A row taller than a page is read before the row that follows it.</summary>
    /// <param name="fixture">The fixture whose record 50 is taller than a page.</param>
    [Theory]
    [InlineData("T300u")]
    [InlineData("X64K")]
    public async Task TheContinuationOfATallRowIsReadBeforeTheNextRow(string fixture)
    {
        var render = await suite.RenderAsync(fixture, "integrity-table");
        var verdict = await suite.JudgeAsync(render);
        Assert.True(verdict.Passed, string.Join(", ", verdict.Classes));

        var words = RenderingSuite.ReadingWords(verdict.Reading);
        var nextRow = words.ToList().IndexOf("51");
        Assert.True(nextRow > 0, $"{fixture}: the row after the tall one did not print.");

        // The counters of the tall row's generated description print nowhere else, so
        // every one of them proves that its part of the row was read before row 51.
        var counters = UniqueText.Tokens(fixture, "description", 600)
            .Where(token => token.Length > 1 && token[0] == 'n' && token[1..].All(char.IsAsciiDigit))
            .ToList();
        Assert.NotEmpty(counters);
        foreach (var counter in counters)
        {
            var position = words.ToList().IndexOf(counter);
            Assert.True(position >= 0, $"{fixture}: the continuation word {counter} never printed.");
            Assert.True(position < nextRow, $"{fixture}: {counter} is read after the next row's number.");
        }
    }
}
