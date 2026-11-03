namespace ReportService.RenderingTests;

/// <summary>
/// The known-bad controls: one change is applied to a document that passes, and the
/// independent judge must fail it with a class the change names. A control that
/// prints but is not caught is a gate that lets a wrong document through, so every
/// one of them is a case of its own.
/// </summary>
[Collection(RenderingCollection.Name)]
public sealed class KnownBadControlTests(RenderingSuite suite)
{
    /// <summary>Every control, the fixture it is applied to and the classes that answer for it.</summary>
    public static TheoryData<string, string, string[]> Controls => new()
    {
        // A row the data holds is not printed.
        { "RowRemoved", "B100", ["row-lost"] },
        // A row is printed twice.
        { "RowDuplicated", "B100", ["row-duplicated"] },
        // A row prints no text at all.
        { "RowWithoutText", "B100", ["row-lost"] },
        // A value the package prints as it is, truncated by one character: the judge
        // reports the value as missing when what it reads is no longer that value.
        { "AnchorTruncated", "B100", ["text-altered", "text-dropped"] },
        // A value with an ampersand, encoded twice so the page shows the escape.
        { "AnchorDoubleEncoded", "A1-esc", ["text-altered"] },
        // A heading the document start prints is dropped.
        { "HeaderAnchorDropped", "B100", ["text-dropped"] },
        // A word of a row is not printed.
        { "NC-W", "B100", ["text-dropped"] },
        // A row's text is printed twice.
        { "NC-L", "B100", ["text-repeated", "text-altered"] },
        // A row is missing from the printed page.
        { "NC-R", "B100", ["row-lost"] },
        // A printed value is altered.
        { "NC-F", "B100", ["text-altered"] },
    };

    /// <summary>The judge refuses the document with a class the control's change names.</summary>
    /// <param name="mutation">The control applied.</param>
    /// <param name="fixture">The fixture it was applied to.</param>
    /// <param name="expected">The classes that answer for the change.</param>
    [Theory]
    [MemberData(nameof(Controls))]
    public async Task TheJudgeRefusesTheControlledDocument(string mutation, string fixture, string[] expected)
    {
        var render = await suite.RenderAsync(fixture, "integrity-table", mutation);
        var verdict = await suite.JudgeAsync(render);

        Assert.False(verdict.Passed, $"{mutation} printed and the judge passed it.");
        Assert.True(
            verdict.Classes.Any(failure => expected.Any(name => failure.Contains(name, StringComparison.Ordinal))),
            $"{mutation}: the judge reported {string.Join(", ", verdict.Classes)}; expected one of {string.Join(", ", expected)}.");
    }
}
