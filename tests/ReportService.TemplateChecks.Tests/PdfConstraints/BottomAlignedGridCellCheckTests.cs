using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplateSource.Checks;
using ReportService.Testing.TemplatePackages;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The grid cell that may split and is aligned to the bottom: the rule refuses only a
/// demonstrated defect — a grid container in the row part, one of its direct children
/// bottom-aligned by a Box Alignment end value, and no declaration that forbids the
/// break — and it names the part and the cell. A table row, an item that is not
/// bottom-aligned, an alignment the layout does not read, a baseline value, an item
/// whose break is avoided, a grid outside the row part and a construct the model
/// cannot place are all admitted.
/// </summary>
public sealed class BottomAlignedGridCellCheckTests
{
    private const string GridRow =
        "<div class=\"grid\"><div class=\"amount\">{{ item.quantity | html.escape }}</div></div>";

    [Theory]
    [InlineData("scriban", ".sbn", "align-self: end", 11)]
    [InlineData("handlebars", ".hbs", "align-self: end", 11)]
    [InlineData("liquid", ".liquid", "align-items: end", 8)]
    public async Task ThePartTheCellAndTheDeclaringRuleAreNamedPerEngine(
        string engine,
        string extension,
        string declaration,
        int line)
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.GridBottom(engine));
        var candidate = Packages.ReadCandidate(package.Directory);
        using var session = new TemplateCheckSession(TimeProvider.System);

        var result = await new BottomAlignedGridCellCheck(session)
            .CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.NotNull(result.Reason);
        Assert.StartsWith("html/row.html" + extension + ":", result.Reason, StringComparison.Ordinal);
        Assert.Contains("the cell class \"amount\" may split across pages", result.Reason, StringComparison.Ordinal);
        Assert.Contains("is aligned to the bottom in the grid layout", result.Reason, StringComparison.Ordinal);
        Assert.Contains("html/styles.css line " + line, result.Reason, StringComparison.Ordinal);
        Assert.Contains(declaration, result.Reason, StringComparison.Ordinal);
        Assert.Contains("Align it to the top.", result.Reason, StringComparison.Ordinal);
        Assert.True(
            Encoding.UTF8.GetByteCount(result.Reason) <= TemplateRefusal.MaximumBytes,
            "the reason must fit the pipeline's limit");
    }

    [Fact]
    public async Task ACellAlignedByItsOwnAlignSelfIsRefused()
    {
        const string stylesheet = """
            .grid { display: grid; }
            .amount { align-self: end; }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("html/styles.css line 2", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("align-self: end", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACellAlignedByTheContainersAlignItemsIsRefused()
    {
        const string stylesheet = """
            .grid { display: grid; align-items: flex-end; }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.False(result.Passed);
        Assert.Contains("html/styles.css line 1", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("align-items: flex-end", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInlineGridIsRefused()
    {
        const string stylesheet = """
            .grid { display: inline-grid; }
            .amount { align-self: self-end; }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.False(result.Passed);
        Assert.Contains("align-self: self-end", result.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("place-self: end;")]
    [InlineData("place-self: safe end;")]
    [InlineData("place-self: end center;")]
    public async Task ThePlaceSelfBlockComponentIsRead(string declaration)
    {
        var result = await CheckAsync(".grid { display: grid; } .amount { " + declaration + " }");

        Assert.False(result.Passed);
        Assert.Contains(declaration.TrimEnd(';'), result.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("place-items: end;")]
    [InlineData("place-items: flex-end center;")]
    public async Task ThePlaceItemsBlockComponentIsRead(string declaration)
    {
        var result = await CheckAsync(".grid { display: grid; " + declaration + " }");

        Assert.False(result.Passed);
        Assert.Contains(declaration.TrimEnd(';'), result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlaceItemsInlineComponentIsNotABlockAlignment()
    {
        // The first value is the block axis; the second is the inline one.
        var result = await CheckAsync(".grid { display: grid; place-items: center end; }");

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnItemThatIsNotBottomAlignedIsAdmitted()
    {
        var result = await CheckAsync(".grid { display: grid; } .amount { align-self: start; }");

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnItemsOwnAlignmentBeatsTheContainers()
    {
        var result = await CheckAsync(".grid { display: grid; align-items: end; } .amount { align-self: start; }");

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnAutoAlignmentDefersToTheContainer()
    {
        var result = await CheckAsync(".grid { display: grid; align-items: end; } .amount { align-self: auto; }");

        Assert.False(result.Passed);
        Assert.Contains("align-items: end", result.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("last baseline")]
    [InlineData("center")]
    public async Task TheBaselineAndNeutralValuesAreAdmitted(string value)
    {
        var result = await CheckAsync(".grid { display: grid; } .amount { align-self: " + value + "; }");

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AVerticalAlignIsNotAGridAlignment()
    {
        // vertical-align has no effect on a grid item, so it demonstrates no defect.
        var result = await CheckAsync(".grid { display: grid; } .amount { vertical-align: bottom; }");

        Assert.True(result.Passed, result.Reason);
    }

    [Theory]
    [InlineData("break-inside: avoid;")]
    [InlineData("page-break-inside: avoid;")]
    public async Task ACellWhoseBreakIsAvoidedIsAdmitted(string declaration)
    {
        var result = await CheckAsync(".grid { display: grid; } .amount { align-self: end; " + declaration + " }");

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnImportantLaterRuleDecidesTheAlignment()
    {
        const string stylesheet = """
            .grid { display: grid; }
            .amount { align-self: end !important; }
            .amount { align-self: start; }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.False(result.Passed);
        Assert.Contains("align-self: end", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALaterRuleOfTheSameSpecificityWinsTheAlignment()
    {
        const string stylesheet = """
            .grid { display: grid; }
            .amount { align-self: end; }
            .amount { align-self: start; }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task ATableRowIsNotAGrid()
    {
        var result = await CheckAsync(".cell { font-family: sans-serif; }");

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AGridOutsideTheRowPartGovernsNothing()
    {
        var result = await CheckAsync(".wrapper { display: grid; align-items: end; }");

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AFeatureConditionedAlignmentNeverRefuses()
    {
        const string stylesheet = """
            .grid { display: grid; }
            @media (min-width: 20cm) { .amount { align-self: end; } }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AFeatureConditionedItemAlignmentBesideAContainerAlignmentIsUndecided()
    {
        // The item's own alignment is conditioned, so the model cannot place it: the
        // cell may paint start when the condition holds, and the container's end no
        // longer demonstrates the defect (C.1's closing sentence, D5).
        const string stylesheet = """
            .grid { display: grid; align-items: end; }
            @media (min-width: 20cm) { .amount { align-self: start; } }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.True(result.Passed, result.Reason);
    }

    [Theory]
    [InlineData(".grid { display: grid; align-items: end; } .amount { align-self: var(--cell-alignment); }")]
    [InlineData(".grid { display: grid; align-items: end; } .amount { align-self: inherit; }")]
    [InlineData(".grid { display: grid; align-items: end; } .amount { align-self: revert-layer; }")]
    [InlineData(".grid { display: grid; align-items: end; } @supports (align-self: start) { .amount { align-self: start; } }")]
    [InlineData(".grid { display: grid; align-items: end; } .amount.{{ item.kind }} { align-self: start; }")]
    public async Task APossibleItemAlignmentBesideAContainerAlignmentIsUndecided(string stylesheet)
    {
        // A reference, a CSS-wide keyword, a support condition and a dynamic subject
        // are all constructs the model cannot place: the item's alignment is undecided.
        var result = await CheckAsync(stylesheet);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AFeatureConditionedContainerAlignmentBesideItsOwnIsUndecided()
    {
        // The container's alignment is itself conditioned, so no placeable winner
        // demonstrates where the item sits.
        const string stylesheet = """
            .grid { display: grid; align-items: end; }
            @media (min-width: 20cm) { .grid { align-items: start; } }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AFeatureConditionedBreakAvoidAdmitsTheCell()
    {
        // The condition may keep the row from splitting, so the defect is not
        // demonstrated and the cell is admitted.
        const string stylesheet = """
            .grid { display: grid; }
            .amount { align-self: end; }
            @media (min-width: 20cm) { .amount { break-inside: avoid; } }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AFeatureConditionedGridIsNotADemonstratedContainer()
    {
        const string stylesheet = """
            @media (min-width: 20cm) { .grid { display: grid; } }
            .amount { align-self: end; }
            """;

        var result = await CheckAsync(stylesheet);

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task ADynamicSubjectIsRecordedAndNeverRefuses()
    {
        var result = await CheckAsync(".grid { display: grid; } .amount.{{ item.kind }} { align-self: end; }");

        Assert.True(result.Passed, result.Reason);
    }

    [Fact]
    public async Task AnInlineDisplayGridAndAnInlineAlignmentAreRead()
    {
        const string row = """
            <div class="grid" style="display: grid"><div class="amount" style="align-self: end">{{ item.quantity | html.escape }}</div></div>
            """;

        var result = await CheckAsync(".grid { margin: 0; }", row);

        Assert.False(result.Passed);
        Assert.Contains("align-self: end", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("html/row.html.sbn line 1", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFirstOffendingItemInDocumentOrderIsNamed()
    {
        const string row = """
            <div class="grid"><div class="first">a</div><div class="second">b</div></div>
            """;
        const string stylesheet = """
            .grid { display: grid; }
            .first, .second { align-self: end; }
            """;

        var result = await CheckAsync(stylesheet, row);

        Assert.False(result.Passed);
        Assert.Contains("the cell class \"first\"", result.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("the cell class \"second\"", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACellWithNoClassNamesItsId()
    {
        const string row = """
            <div class="grid"><div id="total">a</div></div>
            """;

        var result = await CheckAsync(".grid { display: grid; } #total { align-self: end; }", row);

        Assert.False(result.Passed);
        Assert.Contains("the cell id \"total\"", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACellWithNoClassAndNoIdNamesItsTagAndPosition()
    {
        const string row = """
            <div class="grid"><span>a</span><span>b</span></div>
            """;

        var result = await CheckAsync(".grid { display: grid; } span { align-self: end; }", row);

        Assert.False(result.Passed);
        Assert.Contains("the cell tag \"span\" at position 1", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGridDefectTheSampleCannotShowIsRefused()
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.GridBottomSampleInvisible());
        var candidate = Packages.ReadCandidate(package.Directory);
        using var session = new TemplateCheckSession(TimeProvider.System);

        var result = await new BottomAlignedGridCellCheck(session)
            .CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Contains("the cell class \"amount\"", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("html/styles.css line 12", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSampleInvisibleFixtureHoldsRecordsNoRowCanSplit()
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.GridBottomSampleInvisible());
        var candidate = Packages.ReadCandidate(package.Directory);

        var pdf = candidate.Manifest.Outputs?.Pdf
            ?? throw new InvalidOperationException("the fixture declares no PDF output.");
        Assert.Equal("A4", pdf.Paper);
        Assert.Equal("portrait", pdf.Orientation);
        var margins = pdf.MarginMm ?? throw new InvalidOperationException("the fixture declares no margins.");

        // A4 portrait is 297 mm tall; the printable height is what is left between the
        // margins the manifest declares.
        var printable = 297m - (margins.Top ?? 0) - (margins.Bottom ?? 0);
        Assert.Equal(264m, printable);

        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(package.Directory, "sample.json")))!;
        var items = sample["items"]!.AsArray();
        Assert.Equal(PdfConstraintFixtures.SampleInvisibleRecordCount, items.Count);
        Assert.All(
            items,
            item => Assert.True(
                item!.ToJsonString().Length < 64,
                "the sample's records must stay short enough that one row cannot reach a page"));

        var stylesheet = File.ReadAllText(Path.Combine(package.Directory, "html", "styles.css"));
        Assert.Contains(
            "height: " + PdfConstraintFixtures.SampleInvisibleRowHeightMm + "mm;",
            stylesheet,
            StringComparison.Ordinal);
        Assert.True(
            PdfConstraintFixtures.SampleInvisibleRecordCount * PdfConstraintFixtures.SampleInvisibleRowHeightMm < printable,
            "the whole sample must be shorter than the page it is printed on");
    }

    [Fact]
    public async Task TheMultiFaultFixtureBreaksAllThreeConstraints()
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.MultiFault());
        var candidate = Packages.ReadCandidate(package.Directory);
        using var session = new TemplateCheckSession(TimeProvider.System);

        var pageAccounting = await new PageAccountingCheck()
            .CheckAsync(candidate, TestContext.Current.CancellationToken);
        var fontFamily = await new NamedFontFamilyCheck(session)
            .CheckAsync(candidate, TestContext.Current.CancellationToken);
        var gridCell = await new BottomAlignedGridCellCheck(session)
            .CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.False(pageAccounting.Passed);
        Assert.Equal(TemplateCheckCodes.UnsupportedCapability, pageAccounting.Code);
        Assert.False(fontFamily.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, fontFamily.Code);
        Assert.Contains("sans-serif", fontFamily.Reason!, StringComparison.Ordinal);
        Assert.False(gridCell.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, gridCell.Code);
        Assert.Contains("aligned to the bottom", gridCell.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFirstRefusalOfTheMultiFaultVersionIsPageAccountingOnEveryRun()
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.MultiFault());
        var candidate = Packages.ReadCandidate(package.Directory);

        var first = await Packages.RunAsync(Packages.Checks(), candidate);
        var second = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(first.Passed);
        Assert.Equal(TemplateCheckCodes.UnsupportedCapability, first.Code);
        Assert.NotNull(first.Reason);
        Assert.StartsWith("template.yaml:", first.Reason, StringComparison.Ordinal);
        Assert.Contains("page subtotals", first.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.Reason, second.Reason);
    }

    [Fact]
    public void TheCheckIsNamedAndCarriesItsRuleIdentifier()
    {
        using var session = new TemplateCheckSession(TimeProvider.System);

        Assert.Equal("BottomAlignedGridCellCheck", new BottomAlignedGridCellCheck(session).Name);
        Assert.Equal("pdf-grid-cell-alignment", TemplateRefusal.Rules.GridCellAlignment);
        Assert.Equal(TemplateRefusal.Rules.GridCellAlignment, BottomAlignedGridCellCheck.RuleId);
    }

    [Theory]
    [InlineData("scriban")]
    [InlineData("handlebars")]
    [InlineData("liquid")]
    public async Task AControlPackagePassesAllThreePdfRules(string engine)
    {
        using var package = Packages.WritePackage(PdfConstraintFixtures.Control(engine));
        var candidate = Packages.ReadCandidate(package.Directory);
        using var session = new TemplateCheckSession(TimeProvider.System);

        var pageAccounting = await new PageAccountingCheck()
            .CheckAsync(candidate, TestContext.Current.CancellationToken);
        var fontFamily = await new NamedFontFamilyCheck(session)
            .CheckAsync(candidate, TestContext.Current.CancellationToken);
        var gridCell = await new BottomAlignedGridCellCheck(session)
            .CheckAsync(candidate, TestContext.Current.CancellationToken);

        Assert.True(pageAccounting.Passed, engine + ": " + pageAccounting.Reason);
        Assert.Null(pageAccounting.Reason);
        Assert.True(fontFamily.Passed, engine + ": " + fontFamily.Reason);
        Assert.Null(fontFamily.Reason);
        Assert.True(gridCell.Passed, engine + ": " + gridCell.Reason);
        Assert.Null(gridCell.Reason);
    }

    [Fact]
    public void TheCheckRunsLastOfTheThreePdfRules()
    {
        var order = TemplateChecksRegistration.Order;

        Assert.Equal("PageAccountingCheck", order[^4]);
        Assert.Equal("NamedFontFamilyCheck", order[^3]);
        Assert.Equal("BottomAlignedGridCellCheck", order[^2]);
        Assert.Equal("sample-render", order[^1]);

        using var provider = new ServiceCollection()
            .AddSingleton<ReportService.TemplateChecks.Sample.ISampleRenderer>(new ReportService.Testing.TemplateChecks.InMemorySampleRenderer().AlwaysRendered())
            .AddTemplateVersionChecks()
            .BuildServiceProvider();
        var names = provider.GetServices<ITemplateVersionCheck>().Select(check => check.Name).ToList();
        Assert.Equal("PageAccountingCheck", names[^4]);
        Assert.Equal("NamedFontFamilyCheck", names[^3]);
        Assert.Equal("BottomAlignedGridCellCheck", names[^2]);
        Assert.Equal("sample-render", names[^1]);
    }

    private static async Task<TemplateCheckResult> CheckAsync(string stylesheet, string? row = null)
    {
        using var package = TempPackage.Write(
            ("schema.json", Packages.Schema),
            ("sample.json", Packages.Sample),
            ("html/document-start.html.sbn", "<html><body>"),
            ("html/row.html.sbn", row ?? GridRow),
            ("html/document-end.html.sbn", "</body></html>"),
            ("html/styles.css", stylesheet));
        var candidate = package.Candidate(Packages.ManifestJson());
        using var session = new TemplateCheckSession(TimeProvider.System);

        return await new BottomAlignedGridCellCheck(session)
            .CheckAsync(candidate, TestContext.Current.CancellationToken);
    }
}
