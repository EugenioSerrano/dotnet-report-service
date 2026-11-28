using System.Globalization;
using System.Net;
using System.Text;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// Admissible data never trips the render guards. A one-mebibyte header and a two-mebibyte
/// record - each one string of the character whose HTML encoding expands the most per byte of
/// wire JSON - are printed once each, escaped, through every engine and in both ways a template
/// can print the header: inside the record's own call (record mode) or in the block's opening
/// call (block mode). No guard trips, every character is printed, and the characters of the
/// largest call and the bytes of the block are recorded as a fraction of each guard. A template
/// that prints a large value more than once can reach the guard; that is the template's
/// behaviour, which the guard exists to stop, and not admissible data.
/// </summary>
public sealed class GuardMarginAtTheRecordBoundTests
{
    private const int HeaderBytes = 1_048_576;
    private const int RecordBytes = 2_097_152;

    public static TheoryData<string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var engine in new[] { ScribanTemplateAdapter.EngineName, HandlebarsTemplateAdapter.EngineName, FluidTemplateAdapter.EngineName })
        {
            foreach (var fixture in new[] { "ampersand", "apostrophe", "less-than", "e-acute", "emoji", "base64" })
            {
                data.Add(engine, fixture, true);
                data.Add(engine, fixture, false);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AHeaderAndARecordAtTheirBoundsPrintedOnceTripNoGuard(string engine, string fixture, bool recordMode)
    {
        var headerValue = Fill(fixture == "base64" ? "ampersand" : fixture, HeaderBytes - "{\"title\":\"\"}".Length);
        var itemValue = Fill(fixture, RecordBytes - "{\"v\":\"\"}".Length);
        var header = "{\"title\":\"" + headerValue + "\"}";
        var record = "{\"v\":\"" + itemValue + "\"}";
        Assert.Equal(HeaderBytes, Encoding.UTF8.GetByteCount(header));
        Assert.Equal(RecordBytes, Encoding.UTF8.GetByteCount(record));

        var title = Print(engine, "header.title");
        var value = Print(engine, "item.v");
        (TemplatePart Part, string Source)[] parts = recordMode
            ? [(TemplatePart.DocumentStart, "<main>"), (TemplatePart.Row, "<p>" + title + value + "</p>"), (TemplatePart.DocumentEnd, "</main>")]
            : [(TemplatePart.DocumentStart, "<main><h1>" + title + "</h1>"), (TemplatePart.Row, "<p>" + value + "</p>"), (TemplatePart.DocumentEnd, "</main>")];

        var block = await EngineCases.Compose(engine, parts, header, [record]);

        // The output is ASCII, so a fragment's bytes are its call's characters. The figure is
        // recorded before anything is asserted, so a failing case still leaves it.
        var largestCall = block.Fragments.Max(fragment => fragment.Length);
        var callFraction = largestCall / (double)GuardedWriter.MaximumCharactersPerCall;
        var blockFraction = block.Html.Length / (double)GuardedWriter.MaximumBlockBytes;
        var figure = string.Create(
            CultureInfo.InvariantCulture,
            $"guard-margin engine={engine} fixture={fixture} mode={(recordMode ? "record" : "block")} largest-call-characters={largestCall} call-guard-fraction={callFraction:F4} block-bytes={block.Html.Length} block-guard-fraction={blockFraction:F4}");
        TestContext.Current.TestOutputHelper?.WriteLine(figure);
        TestContext.Current.AddAttachment("guard-margin-" + engine + "-" + fixture + "-" + (recordMode ? "record" : "block"), figure);
        if (RunEnvironment.RunDirectory is not null)
        {
            RunEnvironment.WriteEvidence(
                "guard-margin-" + engine + "-" + fixture + "-" + (recordMode ? "record" : "block") + ".txt",
                Encoding.UTF8.GetBytes(figure + Environment.NewLine));
        }

        // Every character printed: the escaped header and the escaped value are whole.
        var escapedHeader = WebUtility.HtmlEncode(headerValue);
        var escapedValue = WebUtility.HtmlEncode(itemValue);
        var text = block.Text;
        Assert.True(
            text.Contains(escapedValue, StringComparison.Ordinal) && text.Contains(escapedHeader, StringComparison.Ordinal),
            string.Create(
                CultureInfo.InvariantCulture,
                $"not printed whole: escaped header {escapedHeader.Length} and value {escapedValue.Length} characters; the block holds {text.Length} characters; ends with [{text[Math.Max(0, text.Length - 40)..]}]"));

        Assert.True(largestCall <= GuardedWriter.MaximumCharactersPerCall, figure);
        Assert.True(block.Html.Length <= GuardedWriter.MaximumBlockBytes, figure);
    }

    [Fact]
    public async Task ScribanPrintsAStringAtTheRecordBoundWithoutCuttingIt()
    {
        var value = new string('a', RecordBytes - "{\"v\":\"\"}".Length);

        var block = await EngineCases.Compose(
            ScribanTemplateAdapter.EngineName,
            [(TemplatePart.Row, "{{ item.v }}")],
            "{}",
            ["{\"v\":\"" + value + "\"}"]);

        Assert.Equal(value, block.Text.Trim());
        Assert.True(value.Length < ScribanTemplateAdapter.LimitToString);
    }

    private static string Print(string engine, string member) =>
        engine == ScribanTemplateAdapter.EngineName ? "{{ " + member + " | html.escape }}" : "{{ " + member + " }}";

    // A JSON string body of exactly the given wire bytes, made of the fixture's character.
    private static string Fill(string fixture, int bytes) => fixture switch
    {
        "ampersand" => new string('&', bytes),
        "apostrophe" => new string('\'', bytes),
        "less-than" => new string('<', bytes),
        "e-acute" => new string('é', bytes / 2) + new string('a', bytes % 2),
        "emoji" => string.Concat(Enumerable.Repeat("\U0001F600", bytes / 4)) + new string('a', bytes % 4),
        "base64" => "data:image/png;base64," + new string('A', bytes - 22),
        _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "no such fixture"),
    };
}
