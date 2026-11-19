using ReportService.TemplateChecks.PdfConstraints;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The stylesheet walk: it reads one declaration per <c>property: value</c> pair,
/// with the file, the line, the rule's selector and its subject, the origin the
/// declaration sits in, its own position, its importance, the at-rule it is under
/// and the two values themselves. Strings and comments never end a declaration,
/// and a text the walk cannot finish is reported as far as it got — never refused.
/// </summary>
public sealed class StylesheetScanTests
{
    [Fact]
    public void ADeclarationAfterACommentHoldingABraceAndASemicolonIsRead()
    {
        const string css = "/* a } and a ; inside a comment */\n.cell { color: red; }";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        var declaration = Assert.Single(declarations);
        Assert.Equal("color", declaration.Property);
        Assert.Equal("red", declaration.Value);
        Assert.Equal(2, declaration.Line);
        Assert.Equal(".cell", declaration.Selector);
    }

    [Fact]
    public void ABraceInsideAStringDoesNotEndTheRule()
    {
        const string css = ".cell::after { content: \"a } b\"; }\n.other { color: blue; }";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        Assert.Equal(2, declarations.Count);
        Assert.Equal("content", declarations[0].Property);
        Assert.Equal("\"a } b\"", declarations[0].Value);
        Assert.Equal(".other", declarations[1].Selector);
    }

    [Fact]
    public void ASemicolonInsideAStringDoesNotEndTheDeclaration()
    {
        const string css = ".cell { content: \"a; b\"; color: red; }";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        Assert.Equal(2, declarations.Count);
        Assert.Equal("\"a; b\"", declarations[0].Value);
        Assert.Equal("color", declarations[1].Property);
    }

    [Fact]
    public void ANestedMediaRuleKeepsItsOwnSelectorAndRecordsItsCondition()
    {
        const string css = "@media print { .cell { color: red; } }";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        var declaration = Assert.Single(declarations);
        Assert.Equal(".cell", declaration.Selector);
        Assert.Equal(".cell", declaration.Subject);
        Assert.Equal(DeclarationCondition.Print, declaration.Condition);
    }

    [Theory]
    [InlineData("print", DeclarationCondition.Print)]
    [InlineData("all", DeclarationCondition.All)]
    [InlineData("screen", DeclarationCondition.Screen)]
    [InlineData("tv", DeclarationCondition.OtherType)]
    [InlineData("(min-width: 20cm)", DeclarationCondition.Feature)]
    public void EachMediaConditionTheModelDistinguishesIsRecorded(string query, DeclarationCondition expected)
    {
        var css = "@media " + query + " { .cell { color: red; } }";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        Assert.Equal(expected, Assert.Single(declarations).Condition);
    }

    [Theory]
    [InlineData("@supports (display: grid)", DeclarationCondition.Supports)]
    [InlineData("@container (min-width: 10cm)", DeclarationCondition.Container)]
    [InlineData("@layer base", DeclarationCondition.Layer)]
    public void TheOtherAtRulesTheModelDistinguishesAreRecorded(string atRule, DeclarationCondition expected)
    {
        var css = atRule + " { .cell { color: red; } }";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        Assert.Equal(expected, Assert.Single(declarations).Condition);
    }

    [Fact]
    public void ADeclarationDirectlyInsideAnAtRuleHasNoSelectorAndNoSubject()
    {
        const string css = "@font-face { font-family: \"Liberation Sans\"; }";

        var declaration = Assert.Single(StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]));

        Assert.Equal(string.Empty, declaration.Selector);
        Assert.Equal(string.Empty, declaration.Subject);
        Assert.Equal("font-family", declaration.Property);
    }

    [Fact]
    public void AnEmptyRuleYieldsNoDeclaration()
    {
        Assert.Empty(StylesheetScan.Read("html/styles.css", ".cell { }", DeclarationSource.Linked, [0]));
    }

    [Fact]
    public void AnAtRuleWithoutABlockYieldsNoDeclaration()
    {
        Assert.Empty(StylesheetScan.Read("html/styles.css", "@import url(\"other.css\");", DeclarationSource.Linked, [0]));
    }

    [Fact]
    public void ASelectorListKeepsItsWholeTextAndItsRightmostSubject()
    {
        const string css = ".amount, .total { color: red; }";

        var declaration = Assert.Single(StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]));

        Assert.Equal(".amount, .total", declaration.Selector);
        Assert.Equal(".total", declaration.Subject);
    }

    [Fact]
    public void ASelectorListWhoseCellIsItsLastMemberNamesTheCell()
    {
        const string css = ".amount, .cell { font-family: \"Liberation Sans\", sans-serif; }";

        var declaration = Assert.Single(StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]));

        Assert.Equal(".cell", declaration.Subject);
        Assert.Equal("font-family", declaration.Property);
    }

    [Fact]
    public void TheLineIsThePropertyLine()
    {
        const string css = ".cell {\n  color: red;\n  padding: 0;\n}";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        Assert.Equal([2, 3], declarations.Select(declaration => declaration.Line));
    }

    [Fact]
    public void ThePropertyIsLowerCased()
    {
        var declaration = Assert.Single(
            StylesheetScan.Read("html/styles.css", ".cell { FONT-FAMILY: serif; }", DeclarationSource.Linked, [0]));

        Assert.Equal("font-family", declaration.Property);
    }

    [Fact]
    public void TheSubjectIsTheRightmostCompoundOfTheSelector()
    {
        const string css = ".items .cell.amount { color: red; }";

        var declaration = Assert.Single(StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]));

        Assert.Equal(".cell.amount", declaration.Subject);
    }

    [Fact]
    public void TheUniversalSelectorIsItsOwnSubject()
    {
        var declaration = Assert.Single(
            StylesheetScan.Read("html/styles.css", "* { color: red; }", DeclarationSource.Linked, [0]));

        Assert.Equal("*", declaration.Subject);
    }

    [Fact]
    public void ImportantIsSplitOffIntoItsOwnField()
    {
        var declaration = Assert.Single(
            StylesheetScan.Read("html/styles.css", ".cell { color: red !important; }", DeclarationSource.Linked, [0]));

        Assert.Equal(DeclarationImportance.Important, declaration.Importance);
        Assert.Equal("red", declaration.Value);
    }

    [Fact]
    public void ACommentInsideAValueIsRemovedFromIt()
    {
        var declaration = Assert.Single(
            StylesheetScan.Read("html/styles.css", ".cell { color: /* why */ red; }", DeclarationSource.Linked, [0]));

        Assert.Equal("red", declaration.Value);
    }

    [Fact]
    public void TheOrderContinuesTheOriginsPosition()
    {
        const string css = ".cell { color: red; padding: 0; }";

        var linked = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [1]);
        var partStyle = StylesheetScan.Read("html/row.html.sbn", css, DeclarationSource.PartStyle, [2, 1]);

        Assert.Equal([1, 1], linked[0].Order);
        Assert.Equal([1, 2], linked[1].Order);
        Assert.Equal([2, 1, 1], partStyle[0].Order);
        Assert.Equal([2, 1, 2], partStyle[1].Order);
    }

    [Fact]
    public void TheSourceIsTheCallers()
    {
        const string css = ".cell { color: red; }";

        Assert.Equal(
            DeclarationSource.Linked,
            Assert.Single(StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0])).Source);
        Assert.Equal(
            DeclarationSource.PartStyle,
            Assert.Single(StylesheetScan.Read("html/row.html.sbn", css, DeclarationSource.PartStyle, [0])).Source);
    }

    [Fact]
    public void AnInlineDeclarationTakesTheElementAsItsSubjectAndTheAttributesLine()
    {
        var declarations = StylesheetScan.ReadInline(
            "html/row.html.sbn",
            line: 4,
            subject: "td.cell",
            text: "font-family: serif; color: red",
            origin: [0, 2]);

        Assert.Equal(2, declarations.Count);
        Assert.All(declarations, declaration => Assert.Equal(DeclarationSource.Inline, declaration.Source));
        Assert.All(declarations, declaration => Assert.Equal("td.cell", declaration.Subject));
        Assert.All(declarations, declaration => Assert.Equal(4, declaration.Line));
        Assert.All(declarations, declaration => Assert.Equal(string.Empty, declaration.Selector));
        Assert.Equal([0, 2, 1], declarations[0].Order);
        Assert.Equal("font-family", declarations[0].Property);
    }

    [Fact]
    public void ATemplateExpressionIsCarriedWholeAndDoesNotOpenABlock()
    {
        const string css = ".cell-{{ item.kind }} {\n  font-family: sans-serif;\n}";

        var declaration = Assert.Single(StylesheetScan.Read("html/styles.css", css, DeclarationSource.PartStyle, [0]));

        Assert.Equal(".cell-{{ item.kind }}", declaration.Selector);
        Assert.Equal("font-family", declaration.Property);
        Assert.Equal("sans-serif", declaration.Value);
        Assert.Equal(2, declaration.Line);
    }

    [Fact]
    public void AnUnfinishedCommentIsReadAsFarAsItGoesAndNeverThrows()
    {
        const string css = ".cell { color: red; }\n/* never closed";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        Assert.Equal("color", Assert.Single(declarations).Property);
    }

    [Fact]
    public void AnUnfinishedRuleIsReadAsFarAsItGoesAndNeverThrows()
    {
        const string css = ".cell { color: red";

        var declarations = StylesheetScan.Read("html/styles.css", css, DeclarationSource.Linked, [0]);

        Assert.Equal("color", Assert.Single(declarations).Property);
    }
}
