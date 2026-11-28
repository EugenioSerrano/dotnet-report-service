using System.Text;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The two trap rules and the warning channel on their own: each trap warns where it is
/// and stays silent on the correct template, positions are those of the fragment's UTF-8
/// bytes, and the channel orders, de-duplicates, bounds and escapes what it prints.
/// </summary>
public sealed class WarningRulesTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoHeader = new Dictionary<string, object?>(StringComparer.Ordinal);

    [Theory]
    [InlineData("height", "41,82")]
    [InlineData("width", "-41,82")]
    [InlineData("x", "12,5px")]
    [InlineData("opacity", "0,5")]
    [InlineData("y", "1.234,5")]
    [InlineData("width", "1,234.50")]
    [InlineData("height", "1,000")]
    [InlineData("HEIGHT", "50,5%")]
    [InlineData("points", "0,41,82 10,20")]
    [InlineData("viewBox", "0 0 41,5 100")]
    [InlineData("style", "fill: red; height: 12,5px")]
    [InlineData("style", "margin-left:1,5em")]
    public void ACultureNumberInANumericPositionIsFound(string attribute, string value)
    {
        Assert.True(CultureNumberAttributeRule.IsCultureNumber(attribute, value));
    }

    [Theory]
    [InlineData("height", "41.82")]
    [InlineData("height", "1000")]
    [InlineData("title", "41,82")]
    [InlineData("alt", "41,82")]
    [InlineData("data-x", "41,82")]
    [InlineData("class", "a,b")]
    [InlineData("points", "0,40 10,20")]
    [InlineData("viewBox", "0,0,100,50")]
    [InlineData("viewBox", "0 0 100 50")]
    [InlineData("fill", "rgb(1,2,3)")]
    [InlineData("style", "fill: rgb(1,2,3); font-family: a, b")]
    [InlineData("d", "M0,0 L10,5")]
    [InlineData("transform", "translate(1,5)")]
    [InlineData("stroke-dasharray", "4,2")]
    public void TheSameCommaElsewhereIsNotFound(string attribute, string value)
    {
        Assert.False(CultureNumberAttributeRule.IsCultureNumber(attribute, value));
    }

    [Fact]
    public void TheOutputWarningNamesThePartTheRecordTheElementTheAttributeAndTheValue()
    {
        var block = Block(
            (FragmentKind.DocumentStart, 0, "<h1>Año 41,82</h1><svg viewBox=\"0 0 10 10\">"),
            (FragmentKind.Row, 1, "<rect height=\"41.82\"/>"),
            (FragmentKind.Row, 2, "<p title=\"41,82\">41,82 €</p><rect height=\"41,82\" width='3'/>"),
            (FragmentKind.DocumentEnd, 0, "</svg>"));

        var lines = AuthoringWarnings.Default.Lines(Input("scriban", block));

        Assert.Equal(
            ["warning: culture-number-in-attribute part=row record=2 element=rect attribute=height value=41,82 - write the number with num_invariant"],
            lines);
    }

    [Fact]
    public void AnEncodedCommaIsDecodedBeforeTheTest()
    {
        var block = Block((FragmentKind.Row, 1, "<rect height=\"41&#44;82\"/>"));

        Assert.Single(AuthoringWarnings.Default.Lines(Input("liquid", block)));
    }

    [Fact]
    public void TheOrderFollowsTheFragmentsBytesNotTheirCharacters()
    {
        // Multi-byte text before the first attribute moves its byte offset, not its order.
        var block = Block(
            (FragmentKind.Row, 1, "<p>ñññññ</p><rect height=\"1,5\"/>"),
            (FragmentKind.Row, 2, "<rect height=\"2,5\"/>"));

        var lines = AuthoringWarnings.Default.Lines(Input("scriban", block));

        Assert.Equal(2, lines.Count);
        Assert.Contains("record=1", lines[0], StringComparison.Ordinal);
        Assert.Contains("record=2", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheChannelPrintsTwentyLinesInDocumentOrderAndTheCountOfTheRest()
    {
        var rows = Enumerable.Range(1, 25)
            .Select(ordinal => (FragmentKind.Row, (long)ordinal, "<rect height=\"" + ordinal + ",5\"/><rect height=\"" + ordinal + ",5\"/>"))
            .ToArray();

        var lines = AuthoringWarnings.Default.Lines(Input("handlebars", Block(rows)));

        Assert.Equal(AuthoringWarnings.MaximumLines + 1, lines.Count);
        Assert.Contains("record=1 ", lines[0], StringComparison.Ordinal);
        Assert.Contains("record=20 ", lines[19], StringComparison.Ordinal);
        Assert.Equal("warning: 5 more warnings not shown", lines[20]);
    }

    [Fact]
    public void ARuleThatFailsIsOneInternalErrorLine()
    {
        var lines = new AuthoringWarnings([new FailingRule()]).Lines(Input("liquid", Block((FragmentKind.Row, 1, "<rect/>"))));

        Assert.Equal(["internal-error: the warning rules could not run (InvalidOperationException)"], lines);
    }

    [Theory]
    [InlineData("41,82", "41,82")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\r\tb", "a\\r\\tb")]
    [InlineData("a\u0001b", "a\\u0001b")]
    [InlineData("a\u2028b", "a\\u2028b")]
    [InlineData("0123456789012345678901234567890123456789", "0123456789012345678901234567890\u2026")]
    public void AValueIsPrintedOnOneEscapedShortLine(string value, string printed)
    {
        Assert.Equal(printed, AuthoringWarning.Printable(value));
    }

    [Fact]
    public void LiquidSizeOnAnObjectWithASizeFieldIsFound()
    {
        var input = Input(
            "liquid",
            Block((FragmentKind.Row, 1, "x")),
            sources: [new("html/row.html.liquid", "<tr>\n  <td>{{ item.size }}</td>\n</tr>")],
            records: [Record(1, ("size", "M"))]);

        Assert.Equal(
            ["warning: reserved-member-shadows-field file=html/row.html.liquid line=2 column=15 expression=item.size field=item.size - in Liquid .size is the member count; read item[\"size\"]"],
            AuthoringWarnings.Default.Lines(input));
    }

    [Fact]
    public void ATagReadingAHeaderObjectsSizeIsFound()
    {
        var header = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["box"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["size"] = "L" },
        };
        var input = Input(
            "liquid",
            Block((FragmentKind.Row, 1, "x")),
            sources: [new("html/document-start.html.liquid", "{% if header.box.size %}big{% endif %}")],
            header: header);

        var line = Assert.Single(AuthoringWarnings.Default.Lines(input));
        Assert.Contains("expression=header.box.size field=header.box.size", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoopVariablesSizeIsFoundWhenTheSampleCarriesTheField()
    {
        var lines = new List<object?>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["size"] = "S" },
        };
        var input = Input(
            "liquid",
            Block((FragmentKind.Row, 1, "x")),
            sources: [new("html/row.html.liquid", "{% for l in item.lines %}{{ l.size }}{% endfor %}")],
            records: [Record(1, ("lines", lines))]);

        var line = Assert.Single(AuthoringWarnings.Default.Lines(input));
        Assert.Contains("expression=l.size field=item.lines[0].size", line, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> SilentReads => new()
    {
        { "liquid", "{{ item[\"size\"] }}" },
        { "liquid", "{{ item.lines.size }}" },
        { "liquid", "{{ \"item.size\" }}" },
        { "liquid", "{{ item.name }} item.size outside an expression" },
        { "liquid", "{{ block.size }} {{ forloop.size }}" },
        { "scriban", "{{ item.size | html.escape }}" },
        { "handlebars", "{{item.size}}" },
    };

    [Theory]
    [MemberData(nameof(SilentReads))]
    public void AReadThatDoesNotShadowAFieldIsSilent(string engine, string source)
    {
        var input = Input(
            engine,
            Block((FragmentKind.Row, 1, "x")),
            sources: [new("html/row.html." + engine, source)],
            records: [Record(1, ("size", "M"), ("lines", new List<object?> { "a" }))]);

        Assert.Empty(AuthoringWarnings.Default.Lines(input));
    }

    [Fact]
    public void LiquidSizeIsSilentWhenNoObjectCarriesTheField()
    {
        var input = Input(
            "liquid",
            Block((FragmentKind.Row, 1, "x")),
            sources: [new("html/row.html.liquid", "{{ item.size }}{% for l in item.lines %}{{ l.size }}{% endfor %}")],
            records: [Record(1, ("lines", new List<object?> { "a", "b" }))]);

        Assert.Empty(AuthoringWarnings.Default.Lines(input));
    }

    private static ComposedBlock Block(params (FragmentKind Kind, long Record, string Text)[] fragments)
    {
        var bytes = new List<byte>();
        var composed = new List<ComposedFragment>();
        foreach (var (kind, record, text) in fragments)
        {
            var encoded = Encoding.UTF8.GetBytes(text);
            composed.Add(new ComposedFragment(kind, bytes.Count, encoded.Length, record));
            bytes.AddRange(encoded);
        }

        return new ComposedBlock([.. bytes], composed);
    }

    private static ComposedRecord Record(long ordinal, params (string Name, object? Value)[] fields) =>
        new(ordinal, fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

    private static WarningInput Input(
        string engine,
        ComposedBlock block,
        IReadOnlyList<KeyValuePair<string, string>>? sources = null,
        IReadOnlyDictionary<string, object?>? header = null,
        IReadOnlyList<ComposedRecord>? records = null) =>
        new(engine, block, sources ?? [], header ?? NoHeader, records ?? []);

    private sealed class FailingRule : IWarningRule
    {
        public IEnumerable<AuthoringWarning> Find(WarningInput input) => throw new InvalidOperationException("boom");
    }
}
