using System.Globalization;
using System.Net;
using System.Text;
using ReportService.Templates;
using ReportService.Templates.Tests;
using ReportService.Testing;

namespace ReportService.PerformanceTests;

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
[Collection(nameof(MemoryMeasured))]
public sealed class GuardMarginAtTheRecordBoundTests
{
    private const int HeaderBytes = 1_048_576;
    private const int RecordBytes = 2_097_152;

    public static TheoryData<string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var engine in new[] { ScribanTemplateAdapter.EngineName, HandlebarsTemplateAdapter.EngineName, FluidTemplateAdapter.EngineName })
        {
            foreach (var fixture in new[] { "ampersand", "apostrophe", "less-than", "e-acute", "emoji", "base64", "accented" })
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

    public static TheoryData<string> Engines => new(ScribanTemplateAdapter.EngineName, HandlebarsTemplateAdapter.EngineName, FluidTemplateAdapter.EngineName);

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task AValuePrintedTwiceInOneCallAboveTheGuardIsATypedRefusalNeverACut(string engine)
    {
        var value = Print(engine, "item.v");
        var refusal = await Assert.ThrowsAsync<TemplateGuardException>(() => EngineCases.Compose(
            engine,
            [(TemplatePart.Row, "<p>" + value + value + "</p>")],
            "{}",
            [AmpersandRecord()]));

        Assert.Equal(TemplateErrorCodes.QuotaExceeded, refusal.Code);
        Assert.Equal("render-output", refusal.Limit);
    }

    [Fact]
    public async Task AScribanValueConvertedAboveTheGuardIsATypedRefusalNeverACut()
    {
        // The escaped value concatenated with itself is one converted value of 20 971 440
        // characters: the engine cuts it to its limit plus an ellipsis, and the guard refuses it.
        var refusal = await Assert.ThrowsAsync<TemplateGuardException>(() => EngineCases.Compose(
            ScribanTemplateAdapter.EngineName,
            [(TemplatePart.Row, "<p>{{ (item.v | html.escape) + (item.v | html.escape) }}</p>")],
            "{}",
            [AmpersandRecord()]));

        Assert.Equal(TemplateErrorCodes.QuotaExceeded, refusal.Code);
        Assert.Equal("render-output", refusal.Limit);
    }

    [Theory]
    [InlineData("{{ item.v | html.escape | string.upcase }}", "upcase")]
    [InlineData("{{ \"[\" + (item.v | html.escape) + \"]\" }}", "concatenation")]
    public async Task NoOtherScribanConversionPathCutsAnEscapedValueAtTheRecordBound(string source, string path)
    {
        var value = new string('&', RecordBytes - "{\"v\":\"\"}".Length);
        var escaped = WebUtility.HtmlEncode(value);
        var expected = path == "upcase" ? escaped.ToUpperInvariant() : "[" + escaped + "]";

        var block = await EngineCases.Compose(ScribanTemplateAdapter.EngineName, [(TemplatePart.Row, source)], "{}", [AmpersandRecord()]);

        var text = block.Text.Trim();
        Assert.True(
            text == expected,
            string.Create(CultureInfo.InvariantCulture, $"{path}: expected {expected.Length} characters, the output has {text.Length}, ending [{text[Math.Max(0, text.Length - 24)..]}]"));
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

    private static string AmpersandRecord() => "{\"v\":\"" + new string('&', RecordBytes - "{\"v\":\"\"}".Length) + "\"}";

    // Mixed accented text of exactly the given wire bytes: two-byte letters and one-byte spaces.
    private static string Accented(int bytes)
    {
        const string pattern = "\u00E1\u00E9\u00ED\u00F3\u00FA\u00F1 ";
        var text = new StringBuilder(bytes);
        var used = 0;
        var index = 0;
        while (true)
        {
            var next = pattern[index++ % pattern.Length];
            var size = next == ' ' ? 1 : 2;
            if (used + size > bytes)
            {
                break;
            }

            text.Append(next);
            used += size;
        }

        return text.Append('a', bytes - used).ToString();
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
        "accented" => Accented(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "no such fixture"),
    };
}
