using ReportService.TemplateChecks.PdfConstraints;

namespace ReportService.TemplateChecks.Tests.PdfConstraints;

/// <summary>
/// The closed vocabulary itself: the generic families and the CSS-wide keywords of
/// the CSS modules they come from, the block-axis end values with their prefixes
/// and the baseline values the rules deliberately do not read, the grid and
/// break-avoiding declarations, the properties the rules are about, and the
/// elements whose content the page never paints. A rule change is one edit here,
/// and this test fails when that edit drifts from the declared list.
/// </summary>
public sealed class PdfConstraintRulesTests
{
    [Fact]
    public void TheGenericFamiliesAreTheCssFontsFourKeywords()
    {
        string[] expected =
        [
            "serif",
            "sans-serif",
            "monospace",
            "cursive",
            "fantasy",
            "system-ui",
            "ui-serif",
            "ui-sans-serif",
            "ui-monospace",
            "ui-rounded",
            "math",
            "emoji",
            "fangsong",
        ];

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            PdfConstraintRules.GenericFontFamilies.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NoGenericKeywordIsAFamilyName()
    {
        Assert.DoesNotContain("Liberation Sans", PdfConstraintRules.GenericFontFamilies);
        Assert.Contains("sans-serif", PdfConstraintRules.GenericFontFamilies);
    }

    [Fact]
    public void TheCssWideKeywordsAreTheFiveOfTheCascade()
    {
        Assert.Equal(
            ["inherit", "initial", "revert", "revert-layer", "unset"],
            PdfConstraintRules.CssWideKeywords.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheReferenceFunctionsAreTheTwoThePackageDoesNotCarry()
    {
        Assert.Equal(["env", "var"], PdfConstraintRules.ReferenceFunctions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheBottomAlignmentValuesAreTheThreeEndValuesWithTheirPrefixes()
    {
        Assert.Equal(
            [
                "end",
                "flex-end",
                "safe end",
                "safe flex-end",
                "safe self-end",
                "self-end",
                "unsafe end",
                "unsafe flex-end",
                "unsafe self-end",
            ],
            PdfConstraintRules.BottomAlignmentValues.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheBaselineValuesAreDeliberatelyNotInTheVocabulary()
    {
        Assert.DoesNotContain("baseline", PdfConstraintRules.BottomAlignmentValues);
        Assert.DoesNotContain("last baseline", PdfConstraintRules.BottomAlignmentValues);
        Assert.DoesNotContain("start", PdfConstraintRules.BottomAlignmentValues);
        Assert.DoesNotContain("center", PdfConstraintRules.BottomAlignmentValues);
    }

    [Fact]
    public void TheGridAndBreakDeclarationsAreTheDocumentedOnes()
    {
        Assert.Equal(["grid", "inline-grid"], PdfConstraintRules.GridContainerValues.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["break-inside", "page-break-inside"],
            PdfConstraintRules.BreakAvoidingProperties.Order(StringComparer.Ordinal));
        Assert.Equal(["avoid"], PdfConstraintRules.BreakAvoidingValues.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePropertiesTheRulesReadAreTheDocumentedOnes()
    {
        Assert.Equal("font-family", PdfConstraintRules.FontFamilyProperty);
        Assert.Equal("font", PdfConstraintRules.FontShorthandProperty);
        Assert.Equal("display", PdfConstraintRules.DisplayProperty);
        Assert.Equal(
            ["align-items", "align-self", "place-items", "place-self"],
            PdfConstraintRules.AlignmentProperties.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NoOtherPropertyIsInTheVocabulary()
    {
        Assert.DoesNotContain("color", PdfConstraintRules.AlignmentProperties);
        Assert.DoesNotContain("width", PdfConstraintRules.BreakAvoidingProperties);
        Assert.DoesNotContain("padding", PdfConstraintRules.BreakAvoidingProperties);
        Assert.DoesNotContain("vertical-align", PdfConstraintRules.AlignmentProperties);
    }

    [Fact]
    public void TheNonRenderedElementsAreTheThreeWhoseContentIsNeverText()
    {
        Assert.Equal(["script", "style", "template"], PdfConstraintRules.NonRenderedElements.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheVocabularyComparesWithoutCase()
    {
        Assert.Contains("SANS-SERIF", PdfConstraintRules.GenericFontFamilies);
        Assert.Contains("End", PdfConstraintRules.BottomAlignmentValues);
        Assert.Contains("GRID", PdfConstraintRules.GridContainerValues);
    }
}
