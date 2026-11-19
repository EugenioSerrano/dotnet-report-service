using ReportService.Templates;

namespace ReportService.Templates.Tests;

/// <summary>
/// The take's parsers: each engine's own parser at the pin, the position a syntax
/// error carries, and the include name each engine resolves. No file is read and
/// nothing renders.
/// </summary>
public sealed class TemplateValidatorsTests
{
    [Fact]
    public void ScribanParsesAPartAndReportsAPosition()
    {
        var validator = new ScribanTemplateValidator();

        Assert.IsType<PartParseResult.Parsed>(validator.Parse("html/row.html.sbn", "{{ item.quantity | html.escape }}"));
        var error = Assert.IsType<PartParseResult.SyntaxError>(validator.Parse("html/row.html.sbn", "{{ if }}"));
        Assert.True(error.Line is null or > 0);
        Assert.Equal("html/row.html.sbn", validator.ResolveInclude("html/row.html.sbn"));
        Assert.Null(validator.ResolveInclude(""));
    }

    [Fact]
    public void HandlebarsParsesAPartAndReportsAPosition()
    {
        var validator = new HandlebarsTemplateValidator();

        Assert.IsType<PartParseResult.Parsed>(validator.Parse("html/row.html.hbs", "{{ item.quantity }}"));
        var error = Assert.IsType<PartParseResult.SyntaxError>(validator.Parse("html/row.html.hbs", "{{#each items}}"));
        Assert.True(error.Line is null or > 0);
        Assert.Equal("partials/head.hbs", validator.ResolveInclude("partials/head"));
    }

    [Fact]
    public void FluidParsesAPartAndReportsAPosition()
    {
        var validator = new FluidTemplateValidator();

        Assert.IsType<PartParseResult.Parsed>(validator.Parse("html/row.html.liquid", "{{ item.quantity }}"));
        var error = Assert.IsType<PartParseResult.SyntaxError>(validator.Parse("html/row.html.liquid", "{% for x in %}"));
        Assert.True(error.Line is null or > 0);
        Assert.Equal("partials/head.liquid", validator.ResolveInclude("partials/head"));
    }

    [Fact]
    public void TheEngineExtensionsAreTheAdrs()
    {
        Assert.Equal(".sbn", new ScribanTemplateValidator().PartExtension);
        Assert.Equal(".hbs", new HandlebarsTemplateValidator().PartExtension);
        Assert.Equal(".liquid", new FluidTemplateValidator().PartExtension);
    }
}
