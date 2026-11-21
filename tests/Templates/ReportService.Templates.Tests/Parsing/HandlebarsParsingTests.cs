using HandlebarsDotNet;

namespace ReportService.Templates.Tests.Parsing;

/// <summary>
/// The one Handlebars configuration the product makes: an empty one, with no helper of
/// the product and no file system, so a take's compile is a parse and nothing reaches
/// the host. The render half adds its own surface to the instance this returns.
/// </summary>
public sealed class HandlebarsParsingTests
{
    [Fact]
    public void TheFactoryAnswersAnInstanceWithNoProductHelperAndNoFileSystem()
    {
        var handlebars = HandlebarsParsing.Create();

        Assert.Null(handlebars.Configuration.FileSystem);
        Assert.DoesNotContain(PresentationFunctionTable.Names, name => handlebars.Configuration.Helpers.ContainsKey(name));
    }

    [Fact]
    public void EveryCallAnswersItsOwnInstance()
    {
        var first = HandlebarsParsing.Create();
        var second = HandlebarsParsing.Create();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void AHelperRegisteredOnOneInstanceIsAbsentFromAFreshOne()
    {
        var used = HandlebarsParsing.Create();
        used.RegisterHelper("format_number", (_, _, _) => { });

        var fresh = HandlebarsParsing.Create();

        Assert.True(used.Configuration.Helpers.ContainsKey("format_number"));
        Assert.False(fresh.Configuration.Helpers.ContainsKey("format_number"));
    }

    [Fact]
    public void TheParseAcceptsAndRefusesWhatTheTakeExpects()
    {
        var handlebars = HandlebarsParsing.Create();

        Assert.NotNull(handlebars.Compile("{{ item.quantity }}"));
        Assert.ThrowsAny<HandlebarsException>(() => handlebars.Compile("{{#each items}}"));
    }
}
