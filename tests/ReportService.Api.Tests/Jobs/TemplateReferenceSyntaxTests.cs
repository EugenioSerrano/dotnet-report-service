using ReportService.Api.Jobs;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The syntax of a template reference at acceptance, value by value: an identifier is a
/// lower-case template identifier that can become a storage key segment and a source
/// folder, and a version, when one is named, is a semantic version. A range, a branch name
/// and a partial version are refused here, before anything reads them, so no request can
/// name a version the service could never take.
/// </summary>
public sealed class TemplateReferenceSyntaxTests
{
    private static readonly string LongestIdentifier = "a" + new string('b', 126) + "9";

    [Theory]
    [InlineData("invoice-energy")]
    [InlineData("pilot-grouped")]
    [InlineData("a")]
    [InlineData("9")]
    [InlineData("invoice.energy")]
    [InlineData("invoice_energy")]
    [InlineData("a.b-c_d")]
    public void AnIdentifierInsideTheGrammarIsAccepted(string identifier) =>
        Assert.True(TemplateReferenceSyntax.IsIdentifier(identifier));

    [Theory]
    [InlineData("Pilot-Grouped")]
    [InlineData(".pilot")]
    [InlineData("pilot.")]
    [InlineData("-pilot")]
    [InlineData("pilot-")]
    [InlineData("pilot grouped")]
    [InlineData("pilot/grouped")]
    [InlineData("pilot.grouped.")]
    [InlineData("planté")]
    [InlineData("")]
    public void AnIdentifierOutsideTheGrammarIsRefused(string identifier) =>
        Assert.False(TemplateReferenceSyntax.IsIdentifier(identifier));

    [Fact]
    public void AnIdentifierOfTheLongestAcceptedLengthIsAccepted()
    {
        Assert.Equal(TemplateReferenceSyntax.MaxIdentifierLength, LongestIdentifier.Length);

        Assert.True(TemplateReferenceSyntax.IsIdentifier(LongestIdentifier));
    }

    [Fact]
    public void AnIdentifierOneCharacterTooLongIsRefused() =>
        Assert.False(TemplateReferenceSyntax.IsIdentifier(string.Concat(LongestIdentifier, "9")));

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.10.0")]
    [InlineData("2.0.0-beta.1")]
    [InlineData("1.0.0+build.5")]
    [InlineData("1.0.0-rc.1+b")]
    [InlineData("0.0.0")]
    public void AVersionThatIsASemanticVersionIsAnExactReference(string version) =>
        Assert.Equal(TemplateReferenceSyntax.ReferenceKind.Exact, TemplateReferenceSyntax.Check("invoice-energy", version));

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1")]
    [InlineData("v1.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("^1.0.0")]
    [InlineData("~1.2")]
    [InlineData(">=1.0.0")]
    [InlineData("1.x")]
    [InlineData("1.0.*")]
    [InlineData("1.0 - 2.0")]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("feature/x")]
    [InlineData("latest")]
    public void ARangeABranchNameOrAPartialVersionIsNotAReference(string version) =>
        Assert.Equal(TemplateReferenceSyntax.ReferenceKind.NotFound, TemplateReferenceSyntax.Check("invoice-energy", version));

    [Fact]
    public void ASemanticVersionOfTheLongestAcceptedLengthIsAccepted()
    {
        var version = "1.0.0+" + new string('a', 122);

        Assert.Equal(128, version.Length);
        Assert.Equal(TemplateReferenceSyntax.ReferenceKind.Exact, TemplateReferenceSyntax.Check("invoice-energy", version));
    }

    [Fact]
    public void ASemanticVersionOneCharacterTooLongIsRefused()
    {
        var version = "1.0.0+" + new string('a', 123);

        Assert.Equal(129, version.Length);
        Assert.Equal(TemplateReferenceSyntax.ReferenceKind.NotFound, TemplateReferenceSyntax.Check("invoice-energy", version));
    }

    [Fact]
    public void AReferenceWithoutAVersionNamesTheTemplate() =>
        Assert.Equal(TemplateReferenceSyntax.ReferenceKind.ByName, TemplateReferenceSyntax.Check("invoice-energy", null));

    [Fact]
    public void AnOutOfGrammarIdentifierIsNotFoundEvenWithAValidVersion() =>
        Assert.Equal(TemplateReferenceSyntax.ReferenceKind.NotFound, TemplateReferenceSyntax.Check("Invoice-Energy", "1.0.0"));

    [Fact]
    public void TheFormOfAReferenceIsTheLogsWordForIt()
    {
        Assert.Equal("exact", TemplateReferenceSyntax.FormOf("1.0.0"));
        Assert.Equal("by-name", TemplateReferenceSyntax.FormOf(null));
    }
}
