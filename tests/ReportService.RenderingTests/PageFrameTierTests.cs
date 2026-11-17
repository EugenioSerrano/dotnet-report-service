namespace ReportService.RenderingTests;

/// <summary>
/// The page-frame tier: the product prints a report with the paper size, the
/// orientation, the margins, the page elements and the numbering its template
/// declares, on the four geometries the service offers, and the independent judge
/// reads all of them back from the printed page. A page frame the package did not
/// declare, a page element that hides the detail, or a manifest that lost its page
/// is refused before anything is published.
/// </summary>
[Trait("Tier", "core")]
[Collection(RenderingCollection.Name)]
public sealed class PageFrameTierTests(RenderingSuite suite)
{
    /// <summary>Every positive case: the fixture and the page-frame package it is printed with.</summary>
    public static TheoryData<string, string> PositiveCases => new()
    {
        { "K1", "page-frame" },
        { "B100", "page-frame" },
        { "K0", "page-frame-a4-landscape" },
        { "K1", "page-frame-a4-landscape" },
        { "B100", "page-frame-a4-landscape" },
        { "T300u", "page-frame-a4-landscape" },
        { "K1", "page-frame-letter-portrait" },
        { "B100", "page-frame-letter-portrait" },
        { "X64K", "page-frame-letter-portrait" },
        { "B100", "page-frame-letter-landscape" },
        { "T300u", "page-frame-letter-landscape" },
        { "X256K", "page-frame-letter-landscape" },
    };

    /// <summary>
    /// The printed document is the page the package declared and passes both readings:
    /// the product's verification and the independent judge.
    /// </summary>
    /// <param name="fixture">The fixture printed.</param>
    /// <param name="package">The page-frame package it is printed with.</param>
    [Theory]
    [MemberData(nameof(PositiveCases))]
    public async Task ThePageFrameIsPrintedAndBothReadingsPass(string fixture, string package)
    {
        var render = await suite.RenderAsync(fixture, package);

        Assert.True(render.VerificationPassed, $"{fixture} printed with {package}: the product's verification refused it ({render.VerificationReason}, page {render.VerificationField}).");
        var verdict = await suite.JudgeAsync(render);

        Assert.True(verdict.Passed, $"{fixture} judged with {package}: {string.Join(", ", verdict.Classes)}");
        Assert.True(verdict.Pages >= 1, $"{fixture} printed no page.");
    }

    /// <summary>An empty report is one page of the template's own page elements and no detail row.</summary>
    [Fact]
    public async Task AnEmptyReportIsOnePageWithItsOwnPageElements()
    {
        var render = await suite.RenderAsync("K0", "page-frame-a4-landscape");
        var verdict = await suite.JudgeAsync(render);

        Assert.True(render.VerificationPassed, render.VerificationReason);
        Assert.True(verdict.Passed, string.Join(", ", verdict.Classes));
        Assert.Equal(1, verdict.Pages);
        Assert.Contains(RenderingSuite.ReadingWords(verdict.Reading), word => string.Equals(word, "Hoja", StringComparison.Ordinal));
    }

    /// <summary>A row taller than a page keeps the column headers and the numbering on every page it continues on.</summary>
    [Fact]
    public async Task ATallRowContinuesWithItsColumnHeadersAndNumbering()
    {
        var render = await suite.RenderAsync("T300u", "page-frame-a4-landscape");
        var verdict = await suite.JudgeAsync(render);

        Assert.True(verdict.Passed, string.Join(", ", verdict.Classes));
        Assert.True(verdict.Pages > 1, "the tall row printed on a single page, so the continuation was not exercised.");
    }

    /// <summary>A page of another size is refused by the product and by the judge.</summary>
    [Fact]
    public async Task APageOfAnotherSizeIsNeverPublished()
    {
        var render = await suite.RenderAsync("B100", "page-frame-a4-landscape", mutation: "PF-SIZE");

        Assert.False(render.VerificationPassed);
        Assert.Equal("page-size", render.VerificationReason);
        var verdict = await suite.JudgeAsync(render);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Classes, failure => failure.Contains("page-geometry-wrong", StringComparison.Ordinal));
    }

    /// <summary>A page element that intrudes into the content area never passes.</summary>
    [Fact]
    public async Task APageElementOverlappingTheDetailNeverPasses()
    {
        var render = await suite.RenderAsync("B100", "page-frame-a4-landscape", mutation: "PF-OVERLAP");

        Assert.False(render.VerificationPassed);
        Assert.Equal("text-extra", render.VerificationReason);
        var verdict = await suite.JudgeAsync(render);
        Assert.False(verdict.Passed);
        Assert.Contains(
            verdict.Classes,
            failure => failure.Contains("structure-lost", StringComparison.Ordinal) || failure.Contains("frame-wrong", StringComparison.Ordinal));
    }

    /// <summary>A package's own page margins are overridden by the service's page rule.</summary>
    [Fact]
    public async Task APackagesOwnMarginsDoNotChangeTheDeclaredPage()
    {
        var render = await suite.RenderAsync("B100", "page-frame-a4-landscape", mutation: "PF-MARGINS");
        var verdict = await suite.JudgeAsync(render);

        Assert.True(render.VerificationPassed, render.VerificationReason);
        Assert.True(verdict.Passed, string.Join(", ", verdict.Classes));
    }

    /// <summary>A manifest that lost its paper size prints nothing and says which member it lacked.</summary>
    [Fact]
    public async Task AManifestWithoutAPaperSizeIsRefusedBeforePrinting()
    {
        var render = await suite.RenderWithoutPageSettingsAsync("B100", "page-frame-a4-landscape");

        Assert.False(render.VerificationPassed);
        Assert.Equal("page-settings-invalid", render.VerificationReason);
        Assert.Equal("outputs.pdf.paper", render.VerificationField);
        Assert.Null(render.Pdf);
        Assert.False(File.Exists(Path.Combine(suite.OutputDirectory, "page-settings-unset.pdf")));
    }

    /// <summary>Three renders of the same document print the same pages and the same words.</summary>
    [Fact]
    public async Task ThreeRendersOfTheSameDocumentAreIdentical()
    {
        await AssertThreeIdenticalRendersAsync("B100", "page-frame-a4-landscape");
    }

    /// <summary>Three renders of the tallest row on the shortest page are identical too.</summary>
    [Fact]
    public async Task ThreeRendersOfATallRowAreIdentical()
    {
        await AssertThreeIdenticalRendersAsync("T300u", "page-frame-letter-landscape");
    }

    private async Task AssertThreeIdenticalRendersAsync(string fixture, string package)
    {
        var readings = new List<(int Pages, IReadOnlyList<string> Words)>();
        for (var run = 1; run <= 3; run++)
        {
            var name = $"{fixture}-{package}-repeat-{run}";
            var render = await suite.RenderAsync(fixture, package, name: name);
            Assert.True(render.VerificationPassed, $"{name}: the product's verification refused it ({render.VerificationReason}).");
            var verdict = await suite.JudgeAsync(render);
            Assert.True(verdict.Passed, $"{name}: {string.Join(", ", verdict.Classes)}");
            readings.Add((verdict.Pages, RenderingSuite.ReadingWords(verdict.Reading)));
        }

        Assert.Equal(readings[0].Pages, readings[1].Pages);
        Assert.Equal(readings[1].Pages, readings[2].Pages);
        Assert.Equal(readings[0].Words, readings[1].Words);
        Assert.Equal(readings[1].Words, readings[2].Words);
    }
}
