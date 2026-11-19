using Scriban;

namespace ReportService.Templates.Tests.Parsing;

/// <summary>
/// The one Scriban parse the product makes: the call the take's validator and the
/// render's adapter both reach, so a file the take accepts is one the render can
/// prepare. No option, limit or file is involved.
/// </summary>
public sealed class ScribanParsingTests
{
    [Fact]
    public void APartTheEngineAcceptsComesBackWithoutErrors()
    {
        var template = ScribanParsing.Parse("html/row.html.sbn", "<tr><td>{{ item.quantity }}</td></tr>");

        Assert.False(template.HasErrors);
    }

    [Fact]
    public void APartTheEngineRefusesIsRefused()
    {
        var template = ScribanParsing.Parse("html/row.html.sbn", "{{ if }}");

        Assert.True(template.HasErrors);
        Assert.NotEmpty(template.Messages);
        Assert.True(template.Messages[0].Span.Start.Line >= 0);
    }

    [Fact]
    public void TheParseIsTheAdaptersOwnConfiguration()
    {
        // The delivered adapter parses through the same call: a part the type accepts is
        // one the adapter prepares, and a part it refuses is refused with the same line.
        var accepted = ScribanParsing.Parse("html/row.html.sbn", "{{ item.quantity }}");
        var refused = ScribanParsing.Parse("html/row.html.sbn", "{{ if }}");

        Assert.False(accepted.HasErrors);
        Assert.True(refused.HasErrors);
        Assert.Equal("html/row.html.sbn", accepted.SourceFilePath);
    }
}
