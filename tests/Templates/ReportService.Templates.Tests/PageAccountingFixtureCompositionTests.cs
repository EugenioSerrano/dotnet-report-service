using System.Text;
using ReportService.Templates;
using ReportService.Testing.TemplatePackages;

namespace ReportService.Templates.Tests;

/// <summary>
/// The page-accounting fixture's parts composed for real with the Scriban engine: the row
/// prints exactly one grid item with its literal amount role, the amount as the context
/// gives it, and the group parts print the group key and the total cell's role inside the
/// block's groups. The static checks parse the parts; this case proves they compose, so a
/// property name the checks cannot evaluate can never reach the rendering cycle.
/// </summary>
public sealed class PageAccountingFixtureCompositionTests
{
    private static readonly PresentationSettings Settings = PresentationSettingsResolver.Resolve(
        new PresentationValues("es-AR", "America/Argentina/Buenos_Aires", DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task TheFixtureComposesTheGridRowTheAmountRoleAndBothGroups()
    {
        var composed = await Compose();

        var text = composed.Text;
        Assert.Contains("data-role=\"amount\"", text, StringComparison.Ordinal);
        Assert.Contains("data-role=\"group-total\"", text, StringComparison.Ordinal);
        Assert.Contains(">10.00</div>", text, StringComparison.Ordinal);
        Assert.Contains(">2.50</div>", text, StringComparison.Ordinal);
        Assert.Contains(">4.25</div>", text, StringComparison.Ordinal);

        // One root element per row, and the amount role on the row's own amount cell.
        var rows = composed.Rows.ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            var rowText = composed.TextOf(row);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(rowText, "data-role=\"amount\""));
            Assert.StartsWith("<div class=\"item\">", rowText, StringComparison.Ordinal);
            Assert.EndsWith("</div>", rowText, StringComparison.Ordinal);
        });

        // Two groups, each opened and closed in the block.
        var groupStarts = composed.Fragments.Where(fragment => fragment.Kind == FragmentKind.GroupStart).ToList();
        var groupEnds = composed.Fragments.Where(fragment => fragment.Kind == FragmentKind.GroupEnd).ToList();
        Assert.Equal(2, groupStarts.Count);
        Assert.Equal(2, groupEnds.Count);
        Assert.Contains("G1", composed.TextOf(groupStarts[0]), StringComparison.Ordinal);
        Assert.Contains("G2", composed.TextOf(groupStarts[1]), StringComparison.Ordinal);
        Assert.Contains("G1", composed.TextOf(groupEnds[0]), StringComparison.Ordinal);
        Assert.Contains("G2", composed.TextOf(groupEnds[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFixturePrintsNothingInAGroupsPartTheDocumentPartsDoNot()
    {
        var composed = await Compose();

        // The document start prints the report title and the column captions the judge reads.
        var start = composed.TextOf(composed.Fragments.First(fragment => fragment.Kind == FragmentKind.DocumentStart));
        Assert.Contains("Page accounting sample", start, StringComparison.Ordinal);
        Assert.Contains("Description", start, StringComparison.Ordinal);

        // The document end closes the document the start opened.
        var end = composed.TextOf(composed.Fragments.First(fragment => fragment.Kind == FragmentKind.DocumentEnd));
        Assert.Contains("</html>", end, StringComparison.Ordinal);
    }

    private static async Task<ComposedBlock> Compose()
    {
        var manifest = new RenderManifest(
            ScribanTemplateAdapter.EngineName,
            ScribanTemplateAdapter.EngineVersion,
            new Dictionary<TemplatePart, string>
            {
                [TemplatePart.DocumentStart] = "html/document-start.html.sbn",
                [TemplatePart.GroupStart] = "html/group-start.html.sbn",
                [TemplatePart.Row] = "html/row.html.sbn",
                [TemplatePart.GroupEnd] = "html/group-end.html.sbn",
                [TemplatePart.DocumentEnd] = "html/document-end.html.sbn",
            },
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: true, VerificationAnchors.None),
            new LocaleDefaults("es-AR", "America/Argentina/Buenos_Aires"),
            PageAccountingFixtures.GroupKey);
        var files = new MemoryPackageFiles(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["html/document-start.html.sbn"] = PageAccountingFixtures.DocumentStart,
            ["html/group-start.html.sbn"] = PageAccountingFixtures.GroupStart,
            ["html/row.html.sbn"] = PageAccountingFixtures.Row,
            ["html/group-end.html.sbn"] = PageAccountingFixtures.GroupEnd,
            ["html/document-end.html.sbn"] = PageAccountingFixtures.DocumentEnd,
        });
        var prepared = new ScribanTemplateAdapter().Prepare(manifest, files);
        var records = new[]
        {
            Record(1, """{"sequence":1,"groupId":"G1","description":"First","amount":10.00}"""),
            Record(2, """{"sequence":2,"groupId":"G1","description":"Second","amount":2.50}"""),
            Record(3, """{"sequence":3,"groupId":"G2","description":"Third","amount":4.25}"""),
        };
        var composer = new BlockComposer(TimeProvider.System);
        return await composer.ComposeAsync(
            new CompositionRequest(
                prepared,
                new CompositionBlock(0, records.Length, 1, IsFirst: true, IsLast: true),
                RenderContextFactory.Header(Encoding.UTF8.GetBytes("""{"reportTitle":"Page accounting sample"}""")),
                records,
                new CompositionRender("es-AR", "America/Argentina/Buenos_Aires", DateTimeOffset.UnixEpoch),
                PageAccountingFixtures.GroupKey),
            new ScribanTemplateAdapter(),
            Settings);
    }

    private static ComposedRecord Record(long ordinal, string json) =>
        new(ordinal, RenderContextFactory.Header(Encoding.UTF8.GetBytes(json)));
}
