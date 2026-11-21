using System.Text;
using System.Text.RegularExpressions;
using ReportService.Data;
using ReportService.TemplateChecks.Sample;
using ReportService.Testing.Fixtures;

namespace ReportService.TemplateChecks.Tests.Sample;

/// <summary>
/// The at-bound builder: the exact decoded size the bound names, the measured word and
/// space counts of the delivered family, determinism, its refusals one byte off, and the
/// values it builds for a pattern and for the one long-value format - the plain text
/// failing the digit pattern, so the construction is what satisfies it.
/// </summary>
public sealed class AtBoundTextTests
{
    [Fact]
    public void TheValueIsExactlyTheBoundInBytes_AndInCharacters()
    {
        var value = AtBoundText.Build(PdfRowBound.Bytes);

        Assert.Equal(PdfRowBound.Bytes, Encoding.UTF8.GetByteCount(value));
        Assert.Equal(PdfRowBound.Bytes, value.Length);
        Assert.Equal(262_144, PdfRowBound.Bytes);
    }

    [Fact]
    public void TheValueHoldsTheMeasuredWordsAndSpaces_WithNoDoubledOrEdgeSpace()
    {
        var value = AtBoundText.Build(PdfRowBound.Bytes);

        Assert.Equal(AtBoundText.Words, value.Split(' ').Length);
        Assert.Equal(AtBoundText.Spaces, value.Count(character => character == ' '));
        Assert.Equal(AtBoundText.Letters, value.Count(character => character != ' '));
        Assert.DoesNotContain("  ", value, StringComparison.Ordinal);
        Assert.False(value.StartsWith(' '));
        Assert.False(value.EndsWith(' '));
        Assert.All(value, character => Assert.True(char.IsAsciiLetter(character) || character == ' '));
    }

    [Fact]
    public void TheValueIsTheFamilyTheDeliveredFixtureLibraryRecorded()
    {
        var value = AtBoundText.Build(PdfRowBound.Bytes);

        Assert.Equal(BoundFamily.Words, value.Split(' ').Length);
        Assert.Equal(BoundFamily.Spaces, value.Count(character => character == ' '));
        Assert.Equal(BoundFamily.Letters(BoundFamily.Ascii), value.Count(character => character != ' '));
        Assert.Equal(BoundFamily.Bytes(BoundFamily.Ascii), Encoding.UTF8.GetByteCount(value));
    }

    [Fact]
    public void TwoBuildsAreByteIdentical()
    {
        Assert.Equal(AtBoundText.Build(PdfRowBound.Bytes), AtBoundText.Build(PdfRowBound.Bytes));
        Assert.Equal(AtBoundText.Sha256(AtBoundText.Build(PdfRowBound.Bytes)), AtBoundText.Sha256(AtBoundText.Build(PdfRowBound.Bytes)));
    }

    [Fact]
    public void TheValueHasTheRecordedSha256()
    {
        Assert.Equal(
            "0a728e0dc9a69fd7319554a1aea9243a97478151b5397b023d5271026eecc429",
            AtBoundText.Sha256(AtBoundText.Build(PdfRowBound.Bytes)));
    }

    [Fact]
    public void TheBuilderRefusesOneByteUnderAndOneByteOver()
    {
        Assert.Throws<AtBoundValueException>(() => AtBoundText.Build(PdfRowBound.Bytes - 1));
        Assert.Throws<AtBoundValueException>(() => AtBoundText.Build(PdfRowBound.Bytes + 1));
    }

    [Theory]
    [InlineData("^[A-Za-z][A-Za-z ]*$")]
    [InlineData("^[0-9][0-9 ]*$")]
    public void ThePatternValueIsExactlyTheBoundAndSatisfiesItsPattern(string pattern)
    {
        var value = AtBoundText.BuildForPattern(pattern, PdfRowBound.Bytes);

        Assert.NotNull(value);
        Assert.Equal(PdfRowBound.Bytes, Encoding.UTF8.GetByteCount(value));
        Assert.Matches(pattern, value);
    }

    [Fact]
    public void ThePlainValueFailsTheDigitPattern_SoTheConstructionIsWhatSatisfiesIt()
    {
        var plain = AtBoundText.Build(PdfRowBound.Bytes);

        Assert.DoesNotMatch("^[0-9][0-9 ]*$", plain);
        Assert.Matches("^[A-Za-z][A-Za-z ]*$", plain);

        var digits = AtBoundText.BuildForPattern("^[0-9][0-9 ]*$", PdfRowBound.Bytes);
        Assert.NotNull(digits);
        Assert.Matches("^[0-9][0-9 ]*$", digits);
    }

    [Fact]
    public void APatternNoConstructedValueSatisfiesAnswersNull()
    {
        Assert.Null(AtBoundText.BuildForPattern("^[0-9]{4}$", PdfRowBound.Bytes));
        Assert.Null(AtBoundText.BuildForPattern("^(abc)+$", PdfRowBound.Bytes));
    }

    [Theory]
    [InlineData("duration")]
    public void ALongValueFormatIsBuiltAtExactlyTheBound(string format)
    {
        var value = AtBoundText.BuildForFormat(format, PdfRowBound.Bytes);

        Assert.NotNull(value);
        Assert.Equal(PdfRowBound.Bytes, Encoding.UTF8.GetByteCount(value));
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("date")]
    [InlineData("time")]
    [InlineData("date-time")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    [InlineData("email")]
    [InlineData("uri")]
    public void ABoundedFormatIsNeverBuilt(string format)
    {
        Assert.Null(AtBoundText.BuildForFormat(format, PdfRowBound.Bytes));
    }

    [Fact]
    public void APatternBuiltNameIsShortAndMatches()
    {
        var name = AtBoundText.BuildForPattern("^[a-z]+$", 4);

        Assert.NotNull(name);
        Assert.Equal(4, name.Length);
        Assert.Matches(new Regex("^[a-z]+$", RegexOptions.CultureInvariant), name);
    }
}
