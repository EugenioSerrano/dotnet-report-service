using System.Text;

namespace ReportService.Templates.Tests;

/// <summary>
/// The three engines are peers that change nothing: the same report written for Scriban,
/// Handlebars and Liquid composes to byte-identical HTML on the same records, with the same
/// dates, amounts and acceptance instant, and three renders of one dataset are identical.
/// The values are the presentation table's, so a difference here is a difference a printed
/// document would carry.
/// </summary>
public sealed class ThreeEngineAgreementTests
{
    private const string Header = """{"title":"Monthly statement","customer":"Acme"}""";

    public static TheoryData<int> RecordCounts => new() { 0, 1, 100 };

    [Theory]
    [MemberData(nameof(RecordCounts))]
    public async Task TheThreeEnginesComposeTheSameReportToByteIdenticalHtml(int recordCount)
    {
        var records = Records(recordCount);

        var scriban = await EngineCases.Compose(ScribanTemplateAdapter.EngineName, Parts(ScribanTemplateAdapter.EngineName), Header, records);
        var handlebars = await EngineCases.Compose(HandlebarsTemplateAdapter.EngineName, Parts(HandlebarsTemplateAdapter.EngineName), Header, records);
        var liquid = await EngineCases.Compose(FluidTemplateAdapter.EngineName, Parts(FluidTemplateAdapter.EngineName), Header, records);

        Assert.Equal(scriban.Text, handlebars.Text);
        Assert.Equal(scriban.Text, liquid.Text);
        Assert.Equal(scriban.Html, handlebars.Html);
        Assert.Equal(scriban.Html, liquid.Html);
    }

    [Fact]
    public async Task TheSameReportIsComposedIdenticallyOnEveryRender()
    {
        var records = Records(100);

        foreach (var engine in new[] { ScribanTemplateAdapter.EngineName, HandlebarsTemplateAdapter.EngineName, FluidTemplateAdapter.EngineName })
        {
            var first = await EngineCases.Compose(engine, Parts(engine), Header, records);
            var second = await EngineCases.Compose(engine, Parts(engine), Header, records);
            var third = await EngineCases.Compose(engine, Parts(engine), Header, records);

            Assert.Equal(first.Html, second.Html);
            Assert.Equal(first.Html, third.Html);
        }
    }

    [Fact]
    public async Task ThePresentationOfTheThreeEnginesIsTheSameTextInBothCulturesAndZones()
    {
        var records = Records(2);

        foreach (var settings in new[] { EngineCases.BuenosAires, EngineCases.Tokyo })
        {
            var texts = new List<string>();
            foreach (var engine in new[] { ScribanTemplateAdapter.EngineName, HandlebarsTemplateAdapter.EngineName, FluidTemplateAdapter.EngineName })
            {
                texts.Add((await EngineCases.Compose(engine, Parts(engine), Header, records, settings)).Text);
            }

            Assert.Equal(texts[0], texts[1]);
            Assert.Equal(texts[0], texts[2]);
        }
    }

    [Fact]
    public async Task TheComposedTextCarriesTheRecordsAndTheAcceptanceInstant()
    {
        var block = await EngineCases.Compose(ScribanTemplateAdapter.EngineName, Parts(ScribanTemplateAdapter.EngineName), Header, Records(2));

        // es-AR, America/Argentina/Buenos_Aires: 1.000,00 and the clock three hours west.
        Assert.Contains("1.001,00", block.Text, StringComparison.Ordinal);
        Assert.Contains("29/10/2026", block.Text, StringComparison.Ordinal);
        Assert.Contains("01/11/2026 23:30", block.Text, StringComparison.Ordinal);
    }

    private static (TemplatePart Part, string Source)[] Parts(string engine) => engine switch
    {
        HandlebarsTemplateAdapter.EngineName =>
        [
            (TemplatePart.DocumentStart, "<table><caption>{{ header.title }}</caption>"),
            (TemplatePart.Row, "<tr><td>{{format_date item.date \"dd/MM/yyyy\"}}</td><td>{{ item.description }}</td><td>{{format_number item.amount \"N2\"}}</td></tr>"),
            (TemplatePart.DocumentEnd, "</table><p>{{format_date render.clock \"dd/MM/yyyy HH:mm\"}}</p>"),
        ],
        FluidTemplateAdapter.EngineName =>
        [
            (TemplatePart.DocumentStart, "<table><caption>{{ header.title }}</caption>"),
            (TemplatePart.Row, "<tr><td>{{ item.date | format_date: \"dd/MM/yyyy\" }}</td><td>{{ item.description }}</td><td>{{ item.amount | format_number: \"N2\" }}</td></tr>"),
            (TemplatePart.DocumentEnd, "</table><p>{{ render.clock | format_date: \"dd/MM/yyyy HH:mm\" }}</p>"),
        ],
        _ =>
        [
            (TemplatePart.DocumentStart, "<table><caption>{{ header.title }}</caption>"),
            (TemplatePart.Row, "<tr><td>{{ format_date item.date \"dd/MM/yyyy\" }}</td><td>{{ item.description }}</td><td>{{ format_number item.amount \"N2\" }}</td></tr>"),
            (TemplatePart.DocumentEnd, "</table><p>{{ format_date render.clock \"dd/MM/yyyy HH:mm\" }}</p>"),
        ],
    };

    private static string[] Records(int count) =>
        [.. Enumerable.Range(1, count).Select(index =>
            Encoding.UTF8.GetString(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ordinal = index,
                    date = "2026-10-30T02:30:00Z",
                    description = "Record " + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    amount = 1000.00m + index,
                })))];
}
