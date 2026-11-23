using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.Rendering.Tests;

/// <summary>
/// The page rule the service writes for one page: the declared paper size, in
/// millimetres and in the invariant culture, and the hash the content security
/// policy allows exactly that rule by. A rule whose bytes are not the ones the hash
/// names, or whose numbers follow the current culture, is a page the template did
/// not declare.
/// </summary>
public sealed class PageRuleTests
{
    // The margins are passed as invariant-culture strings and parsed to decimal in
    // the test body, never as bare numeric literals: a C# attribute cannot carry a
    // decimal literal, so a bare "25.4" is a double, and converting that double to
    // the decimal parameter yields the exact binary expansion
    // (25.399999999999998578914528480) rather than the stored decimal 25.4m. Parsing
    // the string gives the exact decimal the template would store, so the test
    // asserts the product's real rendering ("25.4mm") independently of the platform's
    // double-to-decimal conversion behaviour.
    [Theory]
    [InlineData("A4", "portrait", "15", "12", "18", "12", "@page { size: 210mm 297mm !important; margin: 15mm 12mm 18mm 12mm !important; }")]
    [InlineData("A4", "landscape", "20", "15", "20", "15", "@page { size: 297mm 210mm !important; margin: 20mm 15mm 20mm 15mm !important; }")]
    [InlineData("Letter", "portrait", "25.4", "19.05", "25.4", "19.05", "@page { size: 215.9mm 279.4mm !important; margin: 25.4mm 19.05mm 25.4mm 19.05mm !important; }")]
    [InlineData("Letter", "landscape", "19.05", "25.4", "19.05", "25.4", "@page { size: 279.4mm 215.9mm !important; margin: 19.05mm 25.4mm 19.05mm 25.4mm !important; }")]
    public void TheRuleIsTheDeclaredPageInInvariantMillimetres(
        string paper,
        string orientation,
        string top,
        string right,
        string bottom,
        string left,
        string expected)
    {
        var rule = PageRule.From(Settings(
            paper,
            orientation,
            decimal.Parse(top, CultureInfo.InvariantCulture),
            decimal.Parse(right, CultureInfo.InvariantCulture),
            decimal.Parse(bottom, CultureInfo.InvariantCulture),
            decimal.Parse(left, CultureInfo.InvariantCulture)));

        Assert.Equal(expected, rule.Css);
    }

    [Fact]
    public void TheRuleIsTheSameUnderEveryCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-AR");
            var rule = PageRule.From(Settings("Letter", "landscape", 19.05m, 25.4m, 19.05m, 25.4m));

            Assert.Equal(
                "@page { size: 279.4mm 215.9mm !important; margin: 19.05mm 25.4mm 19.05mm 25.4mm !important; }",
                rule.Css);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TheCspHashIsTheSha256OfTheRulesOwnBytes()
    {
        var rule = PageRule.From(Settings("A4", "portrait", 15, 12, 18, 12));
        var expected = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rule.Css))) + "'";

        Assert.Equal(expected, rule.CspHashSource);
    }

    [Fact]
    public void TheRuleIsAppendedAfterTheDocumentAndChangesNoWordOfIt()
    {
        var rule = PageRule.From(Settings("A4", "landscape", 20, 15, 20, 15));
        var document = "<!DOCTYPE html><html><head></head><body><p>one</p></body></html>";

        var served = rule.AppendTo(document);

        Assert.StartsWith(document, served, StringComparison.Ordinal);
        Assert.EndsWith("<style>" + rule.Css + "</style>", served, StringComparison.Ordinal);
        Assert.Equal(rule.Css, served[(document.Length + "<style>".Length)..^"</style>".Length]);
    }

    [Fact]
    public void ThePolicyAllowsTheRulesHashAndThePackagesOwnOriginOnly()
    {
        var rule = PageRule.From(Settings("A4", "portrait", 15, 12, 18, 12));

        var policy = ChromiumPdfPrinter.PolicyFor(rule);

        Assert.Contains("style-src https://report.invalid " + rule.CspHashSource, policy, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentPageProducesADifferentHash()
    {
        var first = PageRule.From(Settings("A4", "portrait", 15, 12, 18, 12));
        var second = PageRule.From(Settings("A4", "landscape", 15, 12, 18, 12));

        Assert.NotEqual(first.Css, second.Css);
        Assert.NotEqual(first.CspHashSource, second.CspHashSource);
    }

    private static PdfPageSettings Settings(
        string paper,
        string orientation,
        decimal top,
        decimal right,
        decimal bottom,
        decimal left)
    {
        Assert.True(PageOrientations.TryParse(orientation, out var parsed));
        Assert.True(PaperSizes.TryMillimetres(paper, out var width, out var height));
        var portrait = parsed == PageOrientation.Portrait;
        return new PdfPageSettings(
            paper,
            parsed,
            portrait ? width : height,
            portrait ? height : width,
            top,
            right,
            bottom,
            left);
    }
}
