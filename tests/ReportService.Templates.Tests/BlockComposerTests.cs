using System.Text;
using ReportService.Testing;

namespace ReportService.Templates.Tests;

/// <summary>
/// The block composer: the order of the parts in block mode, the group parts at
/// group boundaries, the empty block, and the fragment map the text checks read.
/// </summary>
public sealed class BlockComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    private static readonly (TemplatePart Part, string Source)[] Integrity =
    [
        (TemplatePart.DocumentStart, "<html><body>"),
        (TemplatePart.GroupStart, "<h2>{{ group.key }}</h2>"),
        (TemplatePart.Row, "<tr><td>{{ item.sequence }}</td></tr>"),
        (TemplatePart.GroupEnd, "<p>group end</p>"),
        (TemplatePart.DocumentEnd, "</body></html>"),
    ];

    [Fact]
    public void ABlockIsComposedInTheOrderOfItsParts()
    {
        var block = Compose(Integrity, Records("""{"sequence":1}""", """{"sequence":2}""", """{"sequence":3}"""));

        Assert.Equal(
            [FragmentKind.DocumentStart, FragmentKind.Row, FragmentKind.Row, FragmentKind.Row, FragmentKind.DocumentEnd],
            block.Fragments.Select(fragment => fragment.Kind));
        Assert.Contains("<tr><td>1</td></tr><tr><td>2</td></tr><tr><td>3</td></tr>", block.Text, StringComparison.Ordinal);
        Assert.StartsWith("<html><body>", block.Text, StringComparison.Ordinal);
        Assert.EndsWith("</body></html>", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRowFragmentCarriesTheOrdinalOfItsRecord()
    {
        var block = Compose(Integrity, Records("""{"sequence":8}""", """{"sequence":9}"""));

        Assert.Equal([1L, 2L], block.Rows.Select(fragment => fragment.RecordOrdinal));
        Assert.Equal(0L, block.Fragments[0].RecordOrdinal);
        Assert.Equal(0L, block.Fragments[^1].RecordOrdinal);
    }

    [Fact]
    public void AFragmentNamesItsOwnBytes()
    {
        var block = Compose(Integrity, Records("""{"sequence":1}""", """{"sequence":2}"""));

        foreach (var fragment in block.Fragments)
        {
            Assert.Equal(block.Text.Substring(fragment.Start, fragment.Length), block.TextOf(fragment));
        }

        Assert.Equal(block.Html.Length, block.Fragments[^1].Start + block.Fragments[^1].Length);
    }

    [Fact]
    public void TheGroupPartsWrapEachGroup()
    {
        var block = Compose(
            Integrity,
            Records("""{"groupId":"G1","sequence":1}""", """{"groupId":"G1","sequence":2}""", """{"groupId":"G2","sequence":3}"""),
            groupKey: "groupId");

        Assert.Equal(
            [
                FragmentKind.DocumentStart,
                FragmentKind.GroupStart,
                FragmentKind.Row,
                FragmentKind.Row,
                FragmentKind.GroupEnd,
                FragmentKind.GroupStart,
                FragmentKind.Row,
                FragmentKind.GroupEnd,
                FragmentKind.DocumentEnd,
            ],
            block.Fragments.Select(fragment => fragment.Kind));
        Assert.Equal(1, Count(block, "<h2>G1</h2>"));
        Assert.Equal(1, Count(block, "<h2>G2</h2>"));
        Assert.Equal(2, Count(block, "<p>group end</p>"));
    }

    [Fact]
    public void TheEmptyBlockInvokesOnlyItsDocumentParts()
    {
        var block = Compose(Integrity, Records());

        Assert.Equal([FragmentKind.DocumentStart, FragmentKind.DocumentEnd], block.Fragments.Select(fragment => fragment.Kind));
        Assert.Empty(block.Rows);
        Assert.DoesNotContain(block.DocumentParts, fragment => fragment.Kind == FragmentKind.GroupStart);
        Assert.Equal("<html><body></body></html>", block.Text);
    }

    [Fact]
    public void ABlockThatDoesNotHoldTheRecordsItDeclaresIsRefused()
    {
        var template = Prepare(Integrity);
        var composer = new BlockComposer(new ScribanTemplateAdapter(), new ManualTimeProvider(Now));

        Assert.Throws<TemplateInputException>(() => composer.Compose(new CompositionRequest(
            template,
            new CompositionBlock(0, 2, 1, IsFirst: true, IsLast: true),
            RenderContextFactory.Header("{}"u8.ToArray()),
            Records("""{"sequence":1}"""),
            new CompositionRender("en", "UTC", DateTimeOffset.UnixEpoch),
            GroupKey: null),
            TestContext.Current.CancellationToken));
    }

    private static int Count(ComposedBlock block, string text)
    {
        var count = 0;
        var at = 0;
        while ((at = block.Text.IndexOf(text, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += text.Length;
        }

        return count;
    }

    private static ComposedBlock Compose((TemplatePart Part, string Source)[] parts, ComposedRecord[] records, string? groupKey = null)
    {
        var template = Prepare(parts);
        var composer = new BlockComposer(new ScribanTemplateAdapter(), new ManualTimeProvider(Now));
        return composer.Compose(new CompositionRequest(
            template,
            new CompositionBlock(0, records.Length, records.Length == 0 ? 1 : records[0].Ordinal, IsFirst: true, IsLast: true),
            RenderContextFactory.Header("{}"u8.ToArray()),
            records,
            new CompositionRender("en", "UTC", DateTimeOffset.UnixEpoch),
            groupKey));
    }

    private static ComposedRecord[] Records(params string[] json) =>
        [.. json.Select((record, index) => new ComposedRecord(index + 1, RenderContextFactory.Header(Encoding.UTF8.GetBytes(record))))];

    private static PreparedTemplate Prepare((TemplatePart Part, string Source)[] parts)
    {
        var manifest = new RenderManifest(
            ScribanTemplateAdapter.EngineName,
            ScribanTemplateAdapter.PinnedVersion,
            parts.ToDictionary(part => part.Part, part => part.Part.ToString(), EqualityComparer<TemplatePart>.Default),
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("en", "UTC"),
            parts.Any(part => part.Part == TemplatePart.GroupStart) ? "groupId" : null);
        var files = new MemoryPackageFiles(parts.ToDictionary(part => part.Part.ToString(), part => part.Source, StringComparer.Ordinal));
        return new ScribanTemplateAdapter().Prepare(manifest, files);
    }
}
