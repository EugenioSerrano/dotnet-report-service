using System.Text;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.Pdf.Verification.Tests;

/// <summary>Builds the compositions, the readings and the facts the checks are given.</summary>
internal static class Sample
{
    /// <summary>A block composed from the fragments, in order, with their byte ranges.</summary>
    public static ComposedBlock Block(params (FragmentKind Kind, long Ordinal, string Html)[] fragments)
    {
        var html = string.Concat(fragments.Select(fragment => fragment.Html));
        var mapped = new List<ComposedFragment>(fragments.Length);
        var start = 0;
        foreach (var (kind, ordinal, fragmentHtml) in fragments)
        {
            var length = Encoding.UTF8.GetByteCount(fragmentHtml);
            mapped.Add(new ComposedFragment(kind, start, length, ordinal));
            start += length;
        }

        return new ComposedBlock(Encoding.UTF8.GetBytes(html), mapped);
    }

    /// <summary>A document start, one row per text, and a document end.</summary>
    public static ComposedBlock Rows(params string[] rowTexts)
    {
        var fragments = new List<(FragmentKind, long, string)> { (FragmentKind.DocumentStart, 0, "<p>title</p>") };
        for (var index = 0; index < rowTexts.Length; index++)
        {
            fragments.Add((FragmentKind.Row, index + 1, "<p>" + rowTexts[index] + "</p>"));
        }

        fragments.Add((FragmentKind.DocumentEnd, 0, "<p>end</p>"));
        return Block([.. fragments]);
    }

    /// <summary>The units of <paramref name="texts"/>, in order, each its own row.</summary>
    public static IReadOnlyList<TextUnit> Units(params string[] texts)
    {
        var units = new List<TextUnit>(texts.Length);
        for (var index = 0; index < texts.Length; index++)
        {
            units.Add(new TextUnit(
                texts[index],
                Normalisation.Normalise(texts[index]),
                FragmentIndex: index,
                FragmentKind.Row,
                RecordOrdinal: index + 1,
                IndexInFragment: 0,
                RepeatableHead: false,
                RepeatableFoot: false));
        }

        return units;
    }

    /// <summary>The pages a document prints, already normalised by the aligner's caller.</summary>
    public static IReadOnlyList<string> Pages(params string[] pages) =>
        [.. pages.Select(Normalisation.Normalise)];

    /// <summary>What the source checks are given: the partition, the index, the block and the composition.</summary>
    public static SourceCheckInput Source(
        ComposedBlock composition,
        IReadOnlyList<string>? rowTexts = null,
        VerificationAnchors? anchors = null,
        IReadOnlyDictionary<string, object?>? header = null,
        IReadOnlyList<BlockRecord>? records = null,
        bool isFirst = true,
        long partitionCount = 1,
        long indexCount = 1)
    {
        var text = new TextUnitExtractor().Extract(composition);
        var ordinals = records?.Select(record => record.Ordinal).ToList()
            ?? Enumerable.Range(1, Math.Max(1, (int)indexCount)).Select(ordinal => (long)ordinal).ToList();
        return new SourceCheckInput(
            new PartitionFacts(1, partitionCount),
            new IndexFacts(0, indexCount, 1),
            new BlockFacts(0, 1, records ?? [], header ?? new Dictionary<string, object?>()),
            composition,
            text,
            anchors ?? VerificationAnchors.None,
            isFirst);
    }
}
