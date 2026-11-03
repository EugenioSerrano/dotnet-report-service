namespace ReportService.RenderingTests;

/// <summary>
/// The core tier of the rendering regression suite, printed by the product composer
/// and printer and read by the independent judge: every positive fixture of the
/// native path prints and passes, both markups included. A document that loses a
/// record, alters a value or breaks the page structure fails here, before anything
/// reaches a consumer.
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class CoreTierTests(RenderingSuite suite)
{
    /// <summary>Every positive fixture of the core tier, with the package it is printed with.</summary>
    public static TheoryData<string, string> PositiveFixtures => new()
    {
        { "B100", "integrity-table" },
        { "B1000", "integrity-table" },
        { "K1", "integrity-table" },
        { "T300u", "integrity-table" },
        { "X64K", "integrity-table" },
        { "F26K-a", "integrity-table" },
        { "GE30", "integrity-table" },
        { "X256K", "integrity-table" },
        { "A1-esc", "integrity-table" },
        { "K0", "integrity-table" },
    };

    /// <summary>The judge passes a document the product printed from a valid fixture.</summary>
    /// <param name="fixture">The fixture printed.</param>
    /// <param name="package">The package it was printed with.</param>
    [Theory]
    [MemberData(nameof(PositiveFixtures))]
    public async Task ThePrintedDocumentPassesTheJudge(string fixture, string package)
    {
        var render = await suite.RenderAsync(fixture, package);
        var verdict = await suite.JudgeAsync(render);

        Assert.True(verdict.Passed, $"{fixture} judged with {package}: {string.Join(", ", verdict.Classes)}");
        Assert.True(verdict.Pages >= 1, $"{fixture} printed no page.");
    }
}
