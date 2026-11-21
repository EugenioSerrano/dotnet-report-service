using System.Text;
using ReportService.Data;

namespace ReportService.Templates.Tests;

/// <summary>
/// The render context: every JSON kind becomes a plain value, a number becomes
/// the invariant text of its exact decimal value or its raw token, and a document
/// that is not an object is refused.
/// </summary>
public sealed class RenderContextFactoryTests
{
    private static IReadOnlyDictionary<string, object?> Read(string json) => RenderContextFactory.Header(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void EveryJsonKindBecomesAPlainValue()
    {
        var header = Read("""{"name":"n","whole":42,"flag":true,"off":false,"nothing":null,"nested":{"id":"C1"},"list":[1,"two",false,null]}""");

        Assert.Equal("n", header["name"]);
        Assert.Equal("42", header["whole"]);
        Assert.Equal(true, header["flag"]);
        Assert.Equal(false, header["off"]);
        Assert.Null(header["nothing"]);
        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(header["nested"]);
        Assert.Equal("C1", nested["id"]);
        var list = Assert.IsAssignableFrom<List<object?>>(header["list"]);
        Assert.Equal(4, list.Count);
        Assert.Equal("1", list[0]);
        Assert.Equal("two", list[1]);
        Assert.Null(list[3]);
    }

    [Theory]
    [InlineData("1000.00", "1000.00")]
    [InlineData("-0.50", "-0.50")]
    [InlineData("9007199254740994", "9007199254740994")]
    [InlineData("1000e0", "1000")]
    [InlineData("1.50e1", "15.0")]
    public void ANumberIsTheInvariantTextOfItsExactDecimal(string raw, string expected)
    {
        var item = Read($"{{\"value\":{raw}}}");
        Assert.Equal(expected, item["value"]);
    }

    [Fact]
    public void ANumberWithNoExactDecimalKeepsItsRawToken()
    {
        var item = Read("""{"value":1e400}""");

        Assert.Equal("1e400", item["value"]);
    }

    [Fact]
    public void ADocumentThatIsNotAnObjectIsRefused()
    {
        Assert.Throws<TemplateInputException>(() => Read("[1,2,3]"));
    }

    [Fact]
    public void TheBlockCarriesTheRecordOrdinalOnlyInARow()
    {
        var documentPart = RenderContextFactory.Block(0, 25, 1, isFirst: true, isLast: true);
        var row = RenderContextFactory.Block(0, 25, 1, isFirst: true, isLast: true, recordOrdinal: 7);

        Assert.Equal("0", documentPart["index"]);
        Assert.Equal("25", documentPart["recordCount"]);
        Assert.Equal("1", documentPart["firstRecordOrdinal"]);
        Assert.Equal(true, documentPart["isFirst"]);
        Assert.Equal(true, documentPart["isLast"]);
        Assert.False(documentPart.ContainsKey("recordOrdinal"));
        Assert.Equal("7", row["recordOrdinal"]);
    }

    [Fact]
    public void TheGroupAndRenderMembersAreBuiltFromTheJobValues()
    {
        var group = RenderContextFactory.Group("G0001", first: true, continuesFromPreviousBlock: false, continuesInNextBlock: false);
        var render = RenderContextFactory.Render("es-AR", "America/Argentina/Buenos_Aires", DateTimeOffset.Parse("2026-11-01T09:13:10-03:00", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal("G0001", group["key"]);
        Assert.Equal(true, group["first"]);
        Assert.Equal(false, group["continuesFromPreviousBlock"]);
        Assert.Equal("es-AR", render["culture"]);
        Assert.Equal("America/Argentina/Buenos_Aires", render["timeZone"]);
        Assert.Equal("2026-11-01T12:13:10.0000000+00:00", render["clock"]);
    }

    [Fact]
    public void AMemberPathResolvesThroughObjectsOnly()
    {
        var header = Read("""{"customer":{"id":"C1"},"totals":{"amount":"10.00"},"flat":"x"}""");

        Assert.True(RenderContextFactory.TryResolve(header, "customer.id", out var id));
        Assert.Equal("C1", id);
        Assert.True(RenderContextFactory.TryResolve(header, "flat", out var flat));
        Assert.Equal("x", flat);
        Assert.False(RenderContextFactory.TryResolve(header, "customer.missing", out _));
        Assert.False(RenderContextFactory.TryResolve(header, "customer.id.deeper", out _));
        Assert.True(RenderContextFactory.TryResolve(header, "totals", out var totals));
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(totals);
    }

    [Fact]
    public void ARecordIsReadThroughItsOrdinal()
    {
        var record = new RawRecord(3, Encoding.UTF8.GetBytes("""{"sequence":3}"""), 15, 0);

        var item = RenderContextFactory.Item(record);

        Assert.Equal("3", item["sequence"]);
    }

    [Fact]
    public void TheContextExposesOnlyTheModelMembers()
    {
        var header = Read("""{"a":1}""");
        var context = new RenderContext(
            header,
            Item: null,
            Group: null,
            RenderContextFactory.Block(0, 0, 1, true, true),
            RenderContextFactory.NoAggregates,
            RenderContextFactory.Render(null, null, DateTimeOffset.UnixEpoch));

        var members = context.Members().Select(member => member.Key).ToList();

        Assert.Equal(["header", "item", "group", "block", "aggregates", "render"], members);
        Assert.Equal(["header", "item", "group", "block", "aggregates", "render"], RenderContext.MemberNames);
    }
}
