using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The text units of a composed block: what splits a unit and what does not, what
/// is skipped, how a character reference comes back, and which units a surface
/// repeats at the top or the bottom of a page.
/// </summary>
public sealed class TextUnitExtractorTests
{
    private static IReadOnlyList<TextUnit> Units(ComposedBlock block) => new TextUnitExtractor().Extract(block).Units;

    [Fact]
    public void ABlockElementStartsANewUnit()
    {
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, "<div>first</div><p>second</p>")));

        Assert.Equal(["first", "second"], units.Select(unit => unit.Text));
    }

    [Fact]
    public void AnInlineElementDoesNotSplitAUnit()
    {
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, "<p>a<span>b</span><b>c</b>d</p>")));

        var unit = Assert.Single(units);
        Assert.Equal("abcd", unit.Text);
    }

    [Fact]
    public void TheHeadAndItsContentAreSkipped()
    {
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, "<!DOCTYPE html><html><head><title>t</title><link rel=\"stylesheet\" href=\"x.css\"></head><body>shown</body></html>")));

        var unit = Assert.Single(units);
        Assert.Equal("shown", unit.Text);
    }

    [Fact]
    public void ScriptStyleTemplateAndNoscriptAreSkipped()
    {
        var html = "<body><script>if (a < b) { x = \"<p>\"; }</script><style>p { color: red }</style><template><p>t</p></template><noscript><p>n</p></noscript><p>shown</p></body>";
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, html)));

        var unit = Assert.Single(units);
        Assert.Equal("shown", unit.Text);
    }

    [Fact]
    public void ACommentIsSkipped()
    {
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, "<p>a<!-- hidden <p>text</p> -->b</p>")));

        var unit = Assert.Single(units);
        Assert.Equal("ab", unit.Text);
    }

    [Fact]
    public void ACharacterReferenceIsDecoded()
    {
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, "<p>Tom &amp; Jerry &quot;quoted&quot; &#233;</p>")));

        var unit = Assert.Single(units);
        Assert.Equal("Tom & Jerry \"quoted\" é", unit.Text);
    }

    [Fact]
    public void AUnitWithNoVisibleTextIsDropped()
    {
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, "<p> </p><p>\n\t</p><p>x</p>")));

        var unit = Assert.Single(units);
        Assert.Equal("x", unit.Text);
    }

    [Fact]
    public void ABreakStartsANewUnit()
    {
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, "<p>a<br>b</p><hr>")));

        Assert.Equal(["a", "b"], units.Select(unit => unit.Text));
    }

    [Fact]
    public void ARepeatedHeadAndFootAreMarked()
    {
        var html = "<table><thead><tr><th>seq</th></tr></thead><tbody><tr><td>1</td></tr></tbody><tfoot><tr><td>total</td></tr></tfoot></table>";
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, html)));

        Assert.True(units.Single(unit => unit.Text == "seq").RepeatableHead);
        Assert.True(units.Single(unit => unit.Text == "total").RepeatableFoot);
        Assert.False(units.Single(unit => unit.Text == "1").RepeatableHead);
        Assert.False(units.Single(unit => unit.Text == "1").RepeatableFoot);
    }

    [Fact]
    public void AnAttributeValueWithAGreaterThanSignDoesNotEndTheTag()
    {
        var units = Units(Sample.Block((FragmentKind.DocumentStart, 0, "<p class=\"a>b\">shown</p>")));

        var unit = Assert.Single(units);
        Assert.Equal("shown", unit.Text);
    }

    [Fact]
    public void AUnitCarriesItsFragmentAndItsRecord()
    {
        var block = Sample.Block(
            (FragmentKind.DocumentStart, 0, "<p>title</p>"),
            (FragmentKind.Row, 7, "<p>seven</p>"),
            (FragmentKind.DocumentEnd, 0, "<p>end</p>"));

        var units = Units(block);

        Assert.Equal([0, 1, 2], units.Select(unit => unit.FragmentIndex));
        Assert.Equal([FragmentKind.DocumentStart, FragmentKind.Row, FragmentKind.DocumentEnd], units.Select(unit => unit.Kind));
        Assert.Equal([0L, 7L, 0L], units.Select(unit => unit.RecordOrdinal));
        Assert.Equal([0, 1, 2], units.Select(unit => unit.Text switch { "title" => 0, "seven" => 1, _ => 2 }));
    }

    [Fact]
    public void TheIndexOfAUnitCountsTheUnitsOfItsOwnFragment()
    {
        var block = Sample.Block((FragmentKind.Row, 1, "<p>a</p><p>b</p><p>c</p>"));

        var units = new TextUnitExtractor().Extract(block).OfFragment(0);

        Assert.Equal([0, 1, 2], units.Select(unit => unit.IndexInFragment));
    }

    [Fact]
    public void AnEmptyCompositionHoldsNoUnit()
    {
        Assert.Empty(Units(Sample.Block((FragmentKind.DocumentStart, 0, string.Empty))));
        Assert.Empty(Units(Sample.Block()));
    }
}
