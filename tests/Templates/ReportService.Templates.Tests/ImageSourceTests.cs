using System.Globalization;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The source of an image in the composed text: an image data URI passes on every output, a
/// relative reference passes everywhere but on the printed page, and every other value -
/// whatever its case, its hidden characters or its encoding - ends the composition with
/// <c>IMAGE_SOURCE_INVALID</c>, naming the part, the record and the attribute and never the
/// value. The text is read as it is written, so a tag or a value split across writes at any
/// point is read as one.
/// </summary>
public sealed class ImageSourceTests
{
    private const string Png =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";

    // <svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>, base64.
    private const string ScriptedSvg =
        "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjxzY3JpcHQ+YWxlcnQoMSk8L3NjcmlwdD48L3N2Zz4=";

    private const string Marker = "UNIQUEVALUEMARKER";

    public static TheoryData<string> ImageDataUris =>
    [
        Png,
        "data:image/jpeg;base64,/9j/4AAQ",
        "data:image/gif;base64,R0lGODlhAQABAA==",
        "data:image/webp;base64,UklGRg==",
        ScriptedSvg,
        "DATA:IMAGE/PNG;BASE64,iVBORw0K",
        "Data:Image/Svg+Xml;charset=utf-8;Base64,PHN2Zz4=",
        "data:image/png;name=qr.png;base64,iVBORw0K",
        "data&#58;image/png;base64,iVBO&#x52;w0K",
        "data:image/png;base64,iVBORw0K&#43;&#x2F;AA",
        "data:image/png;base64,iVBORw0K   ",
        "  data:image/png;base64,iVBORw0K",
        "data:image/png;base64,iVBO\nRw0K",
        "data:image/png;base64,",
    ];

    public static TheoryData<string> Relative =>
    [
        "images/logo.png",
        "./logo.png",
        "/logo.png",
        "?x=1",
        "#frag",
        "logo.png?v=data:image/png;base64,AA",
    ];

    public static TheoryData<string> Refused =>
    [
        "https://example.invalid/x.png",
        "http://example.invalid/x.png",
        "HTTPS://example.invalid/x.png",
        "//example.invalid/x.png",
        "\\\\example.invalid/x.png",
        "/\\example.invalid/x.png",
        "\\/example.invalid/x.png",
        "ht\ttp://example.invalid/x.png",
        "java\nscript:alert(1)",
        "javascript:alert(1)",
        "JavaScript:alert(1)",
        " javascript:alert(1)",
        "\u0001javascript:alert(1)",
        "&#106;avascript:alert(1)",
        "data:text/html;base64,PHNjcmlwdD4=",
        "data:image/svg+xml,<svg></svg>",
        "data:image/svg+xml;utf8,%3Csvg%3E",
        "data:image/bmp;base64,Qk0=",
        "data:image/png,iVBORw0K",
        "data:image/png;base64,iVBO Rw0K",
        "data:image/png;base64,iVBO-w0_",
        "data:image/png;base64,iVBORw0K===",
        "data:image/png;base64,iVBORw0",
        "data:image/png;base64,iV=BORw0",
        "data:image/png;base64,iVBO\\w0K",
        "data:;base64,iVBORw0K",
        "data:",
        "mailto:x@example.invalid",
        "",
        "   ",
    ];

    [Theory]
    [MemberData(nameof(ImageDataUris))]
    public void AnImageDataUriPassesOnEveryOutput(string value)
    {
        foreach (var policy in Enum.GetValues<ImageSourcePolicy>())
        {
            Assert.Null(Write(policy, Img(value)));
        }
    }

    [Theory]
    [MemberData(nameof(Relative))]
    public void ARelativeReferenceIsRefusedOnThePrintedPageOnly(string value)
    {
        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, Write(ImageSourcePolicy.Pdf, Img(value))?.Code);
        Assert.Null(Write(ImageSourcePolicy.Html, Img(value)));
        Assert.Null(Write(ImageSourcePolicy.Markdown, Img(value)));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void EveryOtherValueIsRefusedOnEveryOutput(string value)
    {
        foreach (var policy in Enum.GetValues<ImageSourcePolicy>())
        {
            var refusal = Write(policy, Img(value));
            Assert.NotNull(refusal);
            Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, refusal.Code);
        }
    }

    [Theory]
    [InlineData("<img src=\"{0}\">", "src")]
    [InlineData("<IMG SRC=\"{0}\">", "src")]
    [InlineData("<img alt='x' src='{0}'>", "src")]
    [InlineData("<img src={0}>", "src")]
    [InlineData("<img src={0} alt=x>", "src")]
    [InlineData("<image src=\"{0}\">", "src")]
    [InlineData("<svg><image href=\"{0}\"/></svg>", "href")]
    [InlineData("<svg><image xlink:href=\"{0}\"/></svg>", "xlink:href")]
    [InlineData("<svg><image x:href=\"{0}\"/></svg>", "x:href")]
    [InlineData("<svg:image XLINK:HREF=\"{0}\"/>", "xlink:href")]
    [InlineData("<img src=\"data:image/png;base64,iVBORw0K\" src=\"{0}\">", "src")]
    [InlineData("<img\nsrc\n=\n\"{0}\">", "src")]
    [InlineData("<!--> <img src=\"{0}\">", "src")]
    [InlineData("<!-- x --!> <img src=\"{0}\">", "src")]
    [InlineData("<script>x</script ><img src=\"{0}\">", "src")]
    public void EveryImageSourcePositionIsChecked(string markup, string attribute)
    {
        var text = string.Format(CultureInfo.InvariantCulture, markup, "https://example.invalid/" + Marker);

        var refusal = Write(ImageSourcePolicy.Html, text);

        Assert.NotNull(refusal);
        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, refusal.Code);
        Assert.Equal(attribute, refusal.Attribute);
        Assert.DoesNotContain(Marker, refusal.Message, StringComparison.Ordinal);
        Assert.Null(Write(ImageSourcePolicy.Html, string.Format(CultureInfo.InvariantCulture, markup, Png)));
    }

    [Theory]
    [InlineData("<a href=\"https://example.invalid/\">x</a>")]
    [InlineData("<use href=\"https://example.invalid/#a\"/>")]
    [InlineData("<img alt=\"https://example.invalid/\" data-src=\"https://example.invalid/\">")]
    [InlineData("<!-- <img src=\"https://example.invalid/\"> -->")]
    [InlineData("<script>var s = '<img src=\"https://example.invalid/\">';</script>")]
    [InlineData("<style>/* <img src=\"https://example.invalid/\"> */</style>")]
    [InlineData("<p>&lt;img src=\"https://example.invalid/\"&gt;</p>")]
    [InlineData("<p>1 < 2 and <3</p>")]
    [InlineData("<!DOCTYPE html><html><body></body></html>")]
    public void TextThatIsNotAnImageSourceIsNotChecked(string text)
    {
        Assert.Null(Write(ImageSourcePolicy.Pdf, text));
    }

    [Fact]
    public void AnImageSourceWithoutAValueIsRefused()
    {
        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, Write(ImageSourcePolicy.Html, "<img src>")?.Code);
        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, Write(ImageSourcePolicy.Html, "<img src alt=\"x\">")?.Code);
        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, Write(ImageSourcePolicy.Html, "<img src=>")?.Code);
    }

    [Fact]
    public void AnImageSourceLeftOpenAtTheEndOfTheBlockIsRefused()
    {
        var refusal = Write(ImageSourcePolicy.Html, "<img src=\"" + Png, complete: true);

        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, refusal?.Code);
    }

    [Theory]
    [MemberData(nameof(SplitCases))]
    public void ATagSplitAcrossWritesAtAnyPointIsReadAsOne(string text, bool refused)
    {
        for (var split = 0; split <= text.Length; split++)
        {
            var refusal = Write(ImageSourcePolicy.Pdf, [text[..split], text[split..]]);
            Assert.True(refused == refusal is not null, "split at " + split.ToString(CultureInfo.InvariantCulture));
        }

        var bySingleCharacter = Write(ImageSourcePolicy.Pdf, text.Select(character => character.ToString()).ToArray());
        Assert.Equal(refused, bySingleCharacter is not null);
    }

    public static TheoryData<string, bool> SplitCases => new()
    {
        { "<p>a</p><img alt=\"q\" src=\"" + Png + "\"><p>b</p>", false },
        { "<img src=\"data&#58;image/png;base64,iVBORw0K&#43;AAA\">", false },
        { "<svg:image xlink:href='" + ScriptedSvg + "'/>", false },
        { "<img src=\"ht\ttps://example.invalid/x.png\">", true },
        { "<IMG SRC=https://example.invalid/x.png>", true },
        { "<!-- c --><img src=\"/logo.png\">", true },
    };

    [Fact]
    public void TheRefusalNamesThePartTheRecordAndTheAttributeAndNeverTheValue()
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new GuardedWriter(text, TimeProvider.System, ImageSourcePolicy.Pdf);
        writer.BeginPart(TemplatePart.Row, 42);

        var refusal = Assert.Throws<TemplateGuardException>(() => writer.Write(Img("https://example.invalid/" + Marker)));

        Assert.Same(writer.Refusal, refusal);
        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, refusal.Code);
        Assert.Equal("part=row ordinal=42 attribute=src", refusal.Message);
        Assert.Equal(TemplatePart.Row, refusal.Part);
        Assert.Equal(42, refusal.Ordinal);
        Assert.Null(refusal.Limit);
        Assert.DoesNotContain(Marker, text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultWriterIsThePrintedPagesPolicy()
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new GuardedWriter(text, TimeProvider.System);

        var refusal = Assert.Throws<TemplateGuardException>(() => writer.Write(Img("logo.png")));

        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, refusal.Code);
    }

    [Theory]
    [InlineData(ScribanTemplateAdapter.EngineName)]
    [InlineData(HandlebarsTemplateAdapter.EngineName)]
    [InlineData(FluidTemplateAdapter.EngineName)]
    public void EachEnginePrintsAnImageDataUriFromTheRecordAndRefusesAnythingElse(string engine)
    {
        var output = engine == ScribanTemplateAdapter.EngineName
            ? "{{ item.image | html.escape }}"
            : "{{ item.image }}";
        var adapter = EngineCases.AdapterOf(engine);
        var template = EngineCases.Prepare(engine, [(TemplatePart.Row, "<td><img src=\"" + output + "\"></td>")]);

        var printed = Render(adapter, template, Png);
        Assert.Contains("src=\"" + Png + "\"", printed, StringComparison.Ordinal);

        var refusal = Assert.Throws<TemplateGuardException>(() => Render(adapter, template, "javascript:alert(1)"));
        Assert.Equal(TemplateErrorCodes.ImageSourceInvalid, refusal.Code);
        Assert.Equal("part=row ordinal=7 attribute=src", refusal.Message);
    }

    [Fact]
    public async Task TheComposerNamesTheRowAndEndsTheBlock()
    {
        var adapter = EngineCases.AdapterOf(FluidTemplateAdapter.EngineName);
        var template = EngineCases.Prepare(
            FluidTemplateAdapter.EngineName,
            [
                (TemplatePart.DocumentStart, "<html><body>"),
                (TemplatePart.Row, "<img src=\"{{ item.image }}\">"),
                (TemplatePart.DocumentEnd, "</body></html>"),
            ]);
        var request = new CompositionRequest(
            template,
            new CompositionBlock(0, 2, 5, IsFirst: true, IsLast: true),
            new Dictionary<string, object?>(),
            [
                new ComposedRecord(5, new Dictionary<string, object?> { ["image"] = Png }),
                new ComposedRecord(6, new Dictionary<string, object?> { ["image"] = "https://example.invalid/x.png" }),
            ],
            new CompositionRender("en-US", "UTC", DateTimeOffset.UnixEpoch),
            null);

        var refusal = await Assert.ThrowsAsync<TemplateGuardException>(() => new BlockComposer(TimeProvider.System)
            .ComposeAsync(request, adapter, EngineCases.BuenosAires, TestContext.Current.CancellationToken));
        Assert.Equal("part=row ordinal=6 attribute=src", refusal.Message);

        var html = await new BlockComposer(TimeProvider.System).ComposeAsync(
            request with
            {
                Records = [new ComposedRecord(5, new Dictionary<string, object?> { ["image"] = "logo.png" })],
                Block = new CompositionBlock(0, 1, 5, IsFirst: true, IsLast: true),
                ImageSources = ImageSourcePolicy.Html,
            },
            adapter,
            EngineCases.BuenosAires,
            TestContext.Current.CancellationToken);
        Assert.Contains("src=\"logo.png\"", html.Text, StringComparison.Ordinal);
    }

    private static string Img(string value) => "<img src=\"" + value.Replace("\"", "&quot;", StringComparison.Ordinal) + "\">";

    private static TemplateGuardException? Write(ImageSourcePolicy policy, string text, bool complete = false) =>
        Write(policy, [text], complete);

    private static TemplateGuardException? Write(ImageSourcePolicy policy, string[] chunks, bool complete = true)
    {
        using var inner = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new GuardedWriter(inner, TimeProvider.System, policy);
        try
        {
            foreach (var chunk in chunks)
            {
                writer.Write(chunk);
            }

            if (complete)
            {
                writer.Complete();
            }

            return null;
        }
        catch (TemplateGuardException refusal)
        {
            return refusal;
        }
    }

    private static string Render(ITemplateEngineAdapter adapter, PreparedTemplate template, string image)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new GuardedWriter(text, TimeProvider.System, ImageSourcePolicy.Pdf);
        writer.BeginPart(TemplatePart.Row, 7);
        var item = new Dictionary<string, object?>(StringComparer.Ordinal) { ["image"] = image };
        adapter.RenderPart(
            template,
            TemplatePart.Row,
            new RenderContext(
                RenderContextFactory.Header("{}"u8),
                item,
                null,
                RenderContextFactory.Block(0, 1, 7, isFirst: true, isLast: true, 7),
                RenderContextFactory.NoAggregates,
                RenderContextFactory.Render("en-US", "UTC", DateTimeOffset.UnixEpoch)),
            EngineCases.BuenosAires,
            writer,
            CancellationToken.None);
        writer.Complete();
        return text.ToString();
    }
}
