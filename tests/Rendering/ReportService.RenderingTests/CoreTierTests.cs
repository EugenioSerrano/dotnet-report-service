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

    /// <summary>
    /// The judge and the product's own verification both pass a document the product
    /// printed from a valid fixture: the two readings agree on every one of them, and a
    /// disagreement fails the suite.
    /// </summary>
    /// <param name="fixture">The fixture printed.</param>
    /// <param name="package">The package it was printed with.</param>
    [Theory]
    [MemberData(nameof(PositiveFixtures))]
    public async Task ThePrintedDocumentPassesTheJudgeAndTheProductsOwnChecks(string fixture, string package)
    {
        var render = await suite.RenderAsync(fixture, package);
        var verdict = await suite.JudgeAsync(render);

        Assert.True(verdict.Passed, $"{fixture} judged with {package}: {string.Join(", ", verdict.Classes)}");
        Assert.True(verdict.Pages >= 1, $"{fixture} printed no page.");
        Assert.True(
            render.VerificationPassed,
            $"{fixture} printed with {package}: the judge passed it and the product's verification refused it ({render.VerificationReason}).");
    }
}
