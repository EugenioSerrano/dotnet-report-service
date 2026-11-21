using ReportService.TemplateChecks.PdfConstraints;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The detail markup: the elements a row part emits, in document order, each with
/// what identifies it (its tag, its literal classes, its id, where it sits), the
/// declarations its own <c>style</c> attribute carries and where they were written,
/// and whether the page ever paints text inside it. A token that holds a template
/// expression identifies nothing and is recorded as dynamic.
/// </summary>
public sealed class DetailMarkupTests
{
    [Fact]
    public void TheElementsAreReadInDocumentOrder()
    {
        const string part = "<tr><td class=\"cell\">{{ item.quantity }}</td><td class=\"cell\">{{ item.amount }}</td></tr>";

        var elements = DetailMarkup.Read("html/row.html.sbn", part);

        Assert.Equal(["tr", "td", "td"], elements.Select(element => element.Tag));
    }

    [Fact]
    public void TheLiteralClassesAreSplitOnWhitespace()
    {
        var element = Assert.Single(DetailMarkup.Read("html/row.html.sbn", "<td class=\"  cell   amount \">x</td>"));

        Assert.Equal(["cell", "amount"], element.Classes);
    }

    [Fact]
    public void TheIdIsReadAndEmptyWhenAbsent()
    {
        var withId = Assert.Single(DetailMarkup.Read("html/row.html.sbn", "<td id=\"amount\">x</td>"));
        var withoutId = Assert.Single(DetailMarkup.Read("html/row.html.sbn", "<td>x</td>"));

        Assert.Equal("amount", withId.Id);
        Assert.Equal(string.Empty, withoutId.Id);
    }

    [Fact]
    public void ThePositionIsOneBasedAmongSiblings()
    {
        const string part = "<tr><td>a</td><td>b</td></tr>";

        var elements = DetailMarkup.Read("html/row.html.sbn", part);

        Assert.Equal(1, elements[0].Position);
        Assert.Equal(1, elements[1].Position);
        Assert.Equal(2, elements[2].Position);
    }

    [Fact]
    public void TheParentIsTheEnclosingElement()
    {
        const string part = "<tr><td><span>x</span></td></tr>";

        var elements = DetailMarkup.Read("html/row.html.sbn", part);

        Assert.Null(elements[0].Parent);
        Assert.Equal(0, elements[1].Parent);
        Assert.Equal(1, elements[2].Parent);
    }

    [Fact]
    public void AStyleAttributeIsReadWithItsFileAndLine()
    {
        const string part = "<tr>\n  <td class=\"cell\" style=\"font-family: serif\">x</td>\n</tr>";

        var elements = DetailMarkup.Read("html/row.html.sbn", part);
        var cell = elements[1];

        Assert.NotNull(cell.Style);
        Assert.Equal("html/row.html.sbn", cell.Style.File);
        Assert.Equal(2, cell.Style.Line);
        var declaration = Assert.Single(cell.Style.Declarations);
        Assert.Equal("font-family", declaration.Property);
        Assert.Equal(DeclarationSource.Inline, declaration.Source);
        Assert.Equal("td.cell", declaration.Subject);
    }

    [Fact]
    public void AnElementWithoutAStyleAttributeHasNoDeclarations()
    {
        var element = Assert.Single(DetailMarkup.Read("html/row.html.sbn", "<td class=\"cell\">x</td>"));

        Assert.Null(element.Style);
    }

    [Fact]
    public void AnElementHoldingTextIsTextBearing()
    {
        var element = Assert.Single(DetailMarkup.Read("html/row.html.sbn", "<td>{{ item.quantity | html.escape }}</td>"));

        Assert.True(element.TextBearing);
    }

    [Fact]
    public void AnElementHoldingOnlyWhitespaceIsNotTextBearing()
    {
        var element = Assert.Single(DetailMarkup.Read("html/row.html.sbn", "<td>  \n\t </td>"));

        Assert.False(element.TextBearing);
    }

    [Fact]
    public void OnlyTheElementThatCarriesTheTextOfItsOwnContentIsTextBearing()
    {
        const string part = "<td><span>x</span></td>";

        var elements = DetailMarkup.Read("html/row.html.sbn", part);

        Assert.False(elements[0].TextBearing);
        Assert.True(elements[1].TextBearing);
    }

    [Fact]
    public void AnElementCarryingTextOfItsOwnBesideAChildIsTextBearing()
    {
        const string part = "<td>a<span>b</span></td>";

        var elements = DetailMarkup.Read("html/row.html.sbn", part);

        Assert.True(elements[0].TextBearing);
        Assert.True(elements[1].TextBearing);
    }

    [Theory]
    [InlineData("<script>var x = 1;</script>")]
    [InlineData("<style>.a { color: red; }</style>")]
    [InlineData("<template>x</template>")]
    public void ANonRenderedElementIsNeverTextBearing(string part)
    {
        var element = Assert.Single(DetailMarkup.Read("html/row.html.sbn", part));

        Assert.False(element.TextBearing);
    }

    [Fact]
    public void ADynamicClassTokenIsNotPartOfTheIdentityAndIsRecorded()
    {
        var element = Assert.Single(
            DetailMarkup.Read("html/row.html.sbn", "<td class=\"cell {{ item.kind }}\">x</td>"));

        Assert.Equal(["cell"], element.Classes);
        Assert.True(element.DynamicIdentity);
    }

    [Fact]
    public void AnElementWithOnlyDynamicTokensIsIdentifiedByItsTagAndFlagged()
    {
        var element = Assert.Single(
            DetailMarkup.Read("html/row.html.sbn", "<td class=\"{{ item.kind }}\">x</td>"));

        Assert.Empty(element.Classes);
        Assert.Equal("td", element.Tag);
        Assert.True(element.DynamicIdentity);
    }

    [Fact]
    public void AnElementWithNoExpressionIsNotFlaggedAndItsExpressionTextIsNotIdentity()
    {
        var element = Assert.Single(DetailMarkup.Read("html/row.html.sbn", "<td class=\"cell\">x</td>"));

        Assert.False(element.DynamicIdentity);
    }

    [Fact]
    public void AVoidElementIsReadAndCarriesNoText()
    {
        var element = Assert.Single(DetailMarkup.Read("html/row.html.sbn", "<br class=\"gap\">"));

        Assert.Equal("br", element.Tag);
        Assert.False(element.TextBearing);
    }
}
