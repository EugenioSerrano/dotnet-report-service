using ReportService.TemplateSource.Checks;
using ReportService.Testing.TemplateChecks;

namespace ReportService.TemplateChecks.Tests;

/// <summary>
/// A record value written as the source of an image is accepted by the take in every
/// engine; the value itself is checked when the output is rendered.
/// </summary>
public sealed class ImageSourceTakeTests
{
    internal const string PixelPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";

    internal const string ImageSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "contractVersion": { "type": "string" },
            "header": { "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] },
            "items": { "type": "array", "items": { "type": "object", "properties": { "quantity": { "type": "integer" }, "image": { "type": "string" } }, "required": ["quantity", "image"] } }
          },
          "required": ["contractVersion", "header", "items"]
        }
        """;

    internal static string ImageSample(string image) =>
        "{ \"contractVersion\": \"1.0\", \"header\": { \"title\": \"Invoice\" }, \"items\": [ { \"quantity\": 1, \"image\": \"" + image + "\" } ] }";

    internal static string Output(string engine, string path) => engine == "scriban"
        ? "{{ " + path + " | html.escape }}"
        : "{{ " + path + " }}";

    internal static string Version(string engine) => engine switch
    {
        "handlebars" => "2.4.3",
        "liquid" => "2.40.0",
        _ => "7.5.1",
    };

    internal static TempPackage ImagePackage(string engine, string row, string? image = null)
    {
        var extension = Packages.Extension(engine);
        return TempPackage.Write(
            ("schema.json", ImageSchema),
            ("sample.json", ImageSample(image ?? PixelPng)),
            ("html/document-start.html" + extension, "<html><body><table>"),
            ("html/row.html" + extension, row),
            ("html/document-end.html" + extension, "</table></body></html>"),
            ("html/styles.css", "body { font-family: \"Liberation Sans\", sans-serif; }"));
    }

    [Theory]
    [InlineData("liquid", "img", "src")]
    [InlineData("scriban", "img", "src")]
    [InlineData("handlebars", "img", "src")]
    [InlineData("liquid", "image", "href")]
    [InlineData("liquid", "image", "xlink:href")]
    public async Task ADataValueInAnImageSourceIsAcceptedByTheTake(string engine, string tag, string attribute)
    {
        var image = Output(engine, "item.image");
        var quantity = Output(engine, "item.quantity");
        var markup = tag == "img"
            ? "<img " + attribute + "=\"" + image + "\">"
            : "<svg width=\"10\" height=\"10\"><image " + attribute + "=\"" + image + "\" width=\"10\" height=\"10\"/></svg>";
        using var package = ImagePackage(engine, "<tr><td>" + quantity + "</td><td>" + markup + "</td></tr>");
        var candidate = package.Candidate(Packages.ManifestJson(engine, engineVersion: Version(engine)));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.True(result.Passed, result.Reason);
    }

    [Theory]
    [InlineData("<img srcset=\"{0}\">", "srcset")]
    [InlineData("<a href=\"{0}\">x</a>", "href")]
    [InlineData("<img src={0}>", "html/row.html.liquid")]
    [InlineData("<img onerror=\"{0}\">", "onerror")]
    [InlineData("<img style=\"{0}\">", "style")]
    [InlineData("<svg><use href=\"{0}\"/></svg>", "href")]
    [InlineData("<video poster=\"{0}\"></video>", "poster")]
    [InlineData("<iframe src=\"{0}\"></iframe>", "src")]
    public async Task EveryOtherUrlPositionStaysRefusedAtTheTake(string markup, string named)
    {
        var row = "<tr><td>{{ item.quantity }}</td><td>" + string.Format(System.Globalization.CultureInfo.InvariantCulture, markup, "{{ item.image }}") + "</td></tr>";
        using var package = ImagePackage("liquid", row);
        var candidate = package.Candidate(Packages.ManifestJson("liquid", engineVersion: Version("liquid")));

        var result = await Packages.RunAsync(Packages.Checks(), candidate);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains(named, result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("img", "src", true)]
    [InlineData("image", "src", true)]
    [InlineData("image", "href", true)]
    [InlineData("image", "xlink:href", true)]
    [InlineData("svg:image", "x:href", true)]
    [InlineData("img", "srcset", false)]
    [InlineData("img", "href", false)]
    [InlineData("img", "x:src", false)]
    [InlineData("iframe", "src", false)]
    [InlineData("use", "href", false)]
    [InlineData("a", "href", false)]
    [InlineData(null, "src", false)]
    [InlineData("img", null, false)]
    public void TheImageSourcePositionsAreTheContract(string? tag, string? attribute, bool position)
    {
        Assert.Equal(position, ReportService.TemplateChecks.Scanning.HtmlScanner.IsImageSourcePosition(tag, attribute));
    }

    [Theory]
    [InlineData("<IMG SRC=\"x\">", "img")]
    [InlineData("<svg:image xlink:href=\"x\">", "svg:image")]
    [InlineData("<my-tag data-x=\"x\">", "my-tag")]
    public void TheContextNamesTheTagTheValueSitsIn(string text, string tag)
    {
        var offset = text.IndexOf("\"x", StringComparison.Ordinal) + 1;

        var context = ReportService.TemplateChecks.Scanning.HtmlScanner.ContextAt(text, offset, []);

        Assert.Equal(ReportService.TemplateChecks.Scanning.HtmlContextKind.AttributeValueQuoted, context.Kind);
        Assert.Equal(tag, context.TagName);
    }
}
