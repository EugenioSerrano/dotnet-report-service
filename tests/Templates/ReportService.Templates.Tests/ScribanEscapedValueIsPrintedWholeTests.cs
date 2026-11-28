using System.Globalization;
using System.Net;

namespace ReportService.Templates.Tests;

/// <summary>
/// A value printed escaped by a Scriban template is printed whole: every character of its HTML
/// encoding reaches the output, whatever the engine's own string limit. A text value at the PDF
/// row bound made of ampersands, and a long accented text, each escape to more characters than
/// they have bytes; neither may come out shortened.
/// </summary>
public sealed class ScribanEscapedValueIsPrintedWholeTests
{
    [Fact]
    public async Task AnAmpersandValueAtTheRowBoundIsPrintedWhole()
    {
        await AssertPrintedWholeAsync(new string('&', 262_144));
    }

    [Fact]
    public async Task ALongAccentedTextIsPrintedWhole()
    {
        await AssertPrintedWholeAsync(new string('é', 200_000));
    }

    private static async Task AssertPrintedWholeAsync(string value)
    {
        var block = await EngineCases.Compose(
            ScribanTemplateAdapter.EngineName,
            [(TemplatePart.Row, "<p>{{ item.v | html.escape }}</p>")],
            "{}",
            ["{\"v\":\"" + value + "\"}"]);

        var escaped = WebUtility.HtmlEncode(value);
        var text = block.Text.Trim();
        Assert.True(
            text == "<p>" + escaped + "</p>",
            string.Create(
                CultureInfo.InvariantCulture,
                $"the escaped value has {escaped.Length} characters; the output has {text.Length}, ending [{text[Math.Max(0, text.Length - 24)..]}]"));
    }
}
