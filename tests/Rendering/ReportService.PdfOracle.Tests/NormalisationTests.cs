using System.Text;

namespace ReportService.PdfOracle.Tests;

/// <summary>The normal form both sides of a comparison go through, and the three kinds of difference it tells apart.</summary>
public sealed class NormalisationTests
{
    [Theory]
    [InlineData("office", "office")]
    [InlineData("o\uFB03ce", "office")]
    [InlineData("a b\tc\nd", "abcd")]
    [InlineData("a\u00A0b", "ab")]
    public void TheNormalFormAppliesCompatibilityAndDropsEverySpace(string text, string expected)
    {
        Assert.Equal(expected, Normalisation.N(text));
    }

    [Fact]
    public void ATextEqualAfterTheNormalFormIsNoDifference()
    {
        Assert.Null(Normalisation.Compare(Normalisation.N("alpha beta"), Normalisation.N("alpha  beta")));
    }

    [Fact]
    public void TextThePrintedSideLacksIsDropped()
    {
        var difference = Normalisation.Compare("alphabeta", "alpha");

        Assert.NotNull(difference);
        Assert.Equal("text-dropped", difference.Class);
        Assert.Equal(5, difference.Offset);
    }

    [Fact]
    public void TextThePrintedSideRepeatsIsRepeated()
    {
        var difference = Normalisation.Compare("alphabeta", "alphabetabeta");

        Assert.NotNull(difference);
        Assert.Equal("text-repeated", difference.Class);
    }

    [Fact]
    public void TextThatDiffersOnBothSidesIsAltered()
    {
        var difference = Normalisation.Compare("alphabeta", "alphagamma");

        Assert.NotNull(difference);
        Assert.Equal("text-altered", difference.Class);
    }

    [Theory]
    [InlineData("alpha", false)]
    [InlineData("alph\uFFFDa", true)]
    [InlineData("alph\uE000a", true)]
    public void AGlyphTheReaderCouldNotMapIsRecognised(string text, bool expected)
    {
        Assert.Equal(expected, Normalisation.HasUnmappedGlyph(text));
    }

    [Fact]
    public void ACharacterOutsideTheBasicPlaneSurvivesTheNormalForm()
    {
        var text = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("a\uD83D\uDE00b"));

        Assert.Equal(text, Normalisation.N(text));
    }
}
