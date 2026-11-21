namespace ReportService.Pdf.Verification.Tests;

/// <summary>
/// The single normalisation both sides of a check are put through, and the
/// characters that are never compared because the document does not map them.
/// </summary>
public sealed class NormalisationTests
{
    [Fact]
    public void CompatibilityFormsAreFolded()
    {
        Assert.Equal("fi", Normalisation.Normalise("ﬁ"));
        Assert.Equal("A", Normalisation.Normalise("Ａ"));
        Assert.Equal("1", Normalisation.Normalise("①"));
    }

    [Fact]
    public void EveryWhiteSpaceCharacterIsRemoved()
    {
        Assert.Equal("ab", Normalisation.Normalise(" a\tb\r\n"));
        Assert.Equal("ab", Normalisation.Normalise("a\u00A0b"));
        Assert.Equal("ab", Normalisation.Normalise("a\u2003b"));
    }

    [Fact]
    public void CaseAndPunctuationAreKept()
    {
        Assert.Equal("AB,c-d.", Normalisation.Normalise("A B, c-d."));
        Assert.NotEqual(Normalisation.Normalise("alpha"), Normalisation.Normalise("Alpha"));
    }

    [Fact]
    public void AnEmptyTextNormalisesToEmpty()
    {
        Assert.Equal(string.Empty, Normalisation.Normalise(string.Empty));
        Assert.Equal(string.Empty, Normalisation.Normalise(" \n "));
    }

    [Fact]
    public void AReplacementCharacterIsAnUnmappedGlyph()
    {
        Assert.True(Normalisation.HasUnmappedGlyph("a\uFFFDb"));
    }

    [Fact]
    public void APrivateUseCharacterIsAnUnmappedGlyph()
    {
        Assert.True(Normalisation.HasUnmappedGlyph("a\uE000b"));
        Assert.True(Normalisation.HasUnmappedGlyph("a\uDB80\uDC00b"));
    }

    [Fact]
    public void OrdinaryTextHoldsNoUnmappedGlyph()
    {
        Assert.False(Normalisation.HasUnmappedGlyph("Tom & Jerry \"quoted\" é"));
        Assert.False(Normalisation.HasUnmappedGlyph(string.Empty));
    }

    [Fact]
    public void AWhiteSpaceCharacterIsReported()
    {
        Assert.True(Normalisation.HasWhiteSpace("a b"));
        Assert.False(Normalisation.HasWhiteSpace("ab"));
    }
}
