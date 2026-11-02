using System.Text;

namespace ReportService.Templates;

/// <summary>A kind of composed fragment, in the order the composer writes them.</summary>
public enum FragmentKind
{
    /// <summary>The document start.</summary>
    DocumentStart,

    /// <summary>The start of a group.</summary>
    GroupStart,

    /// <summary>One record's row.</summary>
    Row,

    /// <summary>The end of a group.</summary>
    GroupEnd,

    /// <summary>The document end.</summary>
    DocumentEnd,
}

/// <summary>One fragment of the composed block, as its byte range in the composed document.</summary>
/// <param name="Kind">Which fragment it is.</param>
/// <param name="Start">The byte offset of its first byte.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="RecordOrdinal">The record it belongs to, or 0 for a document or group fragment.</param>
public sealed record ComposedFragment(FragmentKind Kind, int Start, int Length, long RecordOrdinal);

/// <summary>A composed block: the HTML a printed page is laid out from, and the map of its fragments.</summary>
public sealed class ComposedBlock(byte[] html, IReadOnlyList<ComposedFragment> fragments)
{
    /// <summary>The composed document, in the encoding it was composed in.</summary>
    public byte[] Html { get; } = html;

    /// <summary>The fragments in document order.</summary>
    public IReadOnlyList<ComposedFragment> Fragments { get; } = fragments;

    /// <summary>The composed document as text.</summary>
    public string Text => Encoding.UTF8.GetString(Html);

    /// <summary>The row fragments, in document order — one per record of the block.</summary>
    public IEnumerable<ComposedFragment> Rows => Fragments.Where(fragment => fragment.Kind == FragmentKind.Row);

    /// <summary>The fragments that belong to a document part (the start and the end), group parts excluded.</summary>
    public IEnumerable<ComposedFragment> DocumentParts =>
        Fragments.Where(fragment => fragment.Kind is FragmentKind.DocumentStart or FragmentKind.DocumentEnd);

    /// <summary>The text of one fragment.</summary>
    public string TextOf(ComposedFragment fragment) => Encoding.UTF8.GetString(Html, fragment.Start, fragment.Length);
}

/// <summary>One record to compose: its ordinal in the snapshot and its plain values.</summary>
/// <param name="Ordinal">The record's ordinal, from 1.</param>
/// <param name="Values">The record's members as plain values.</param>
public sealed record ComposedRecord(long Ordinal, IReadOnlyDictionary<string, object?> Values);

/// <summary>The block being composed.</summary>
/// <param name="Index">The block's index within the job.</param>
/// <param name="RecordCount">How many records the block holds.</param>
/// <param name="FirstRecordOrdinal">The ordinal of its first record.</param>
/// <param name="IsFirst">Whether it is the first block of the job.</param>
/// <param name="IsLast">Whether it is the last block of the job.</param>
public sealed record CompositionBlock(int Index, long RecordCount, long FirstRecordOrdinal, bool IsFirst, bool IsLast);

/// <summary>The fixed render values of the job.</summary>
/// <param name="Culture">The job's culture.</param>
/// <param name="TimeZone">The job's time zone.</param>
/// <param name="Clock">The job's fixed timestamp, in UTC.</param>
public sealed record CompositionRender(string? Culture, string? TimeZone, DateTimeOffset Clock);

/// <summary>One composition: the prepared package, the block, the header and the records.</summary>
/// <param name="Template">The prepared package.</param>
/// <param name="Block">The block being composed.</param>
/// <param name="Header">The header as received.</param>
/// <param name="Records">The block's records, in ordinal order.</param>
/// <param name="Render">The job's fixed render values.</param>
/// <param name="GroupKey">The record field that begins a group, or <see langword="null"/> when the package declares none.</param>
public sealed record CompositionRequest(
    PreparedTemplate Template,
    CompositionBlock Block,
    IReadOnlyDictionary<string, object?> Header,
    IReadOnlyList<ComposedRecord> Records,
    CompositionRender Render,
    string? GroupKey);

/// <summary>
/// Composes one block in block mode: the document start, then per record the
/// group start at a group start, the row, and the group end at a group end, then
/// the document end. A block with no records invokes only the document parts.
/// The composed document is returned with the map of its fragments — the byte
/// range, the kind and the record ordinal of each — which the text checks and
/// the verification read.
/// </summary>
public sealed class BlockComposer(ITemplateEngineAdapter adapter, TimeProvider clock)
{
    /// <summary>Composes <paramref name="request"/>.</summary>
    public ComposedBlock Compose(CompositionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Block.RecordCount != request.Records.Count)
        {
            throw new TemplateInputException(
                $"The block declares {request.Block.RecordCount} records and {request.Records.Count} were given to compose.");
        }

        var fragments = new List<ComposedFragment>(request.Records.Count * 2 + 4);
        using var stream = new MemoryStream(64 * 1024);
        using var text = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1 << 16, leaveOpen: true);
        var writer = new GuardedWriter(text, clock);

        Compose(request, TemplatePart.DocumentStart, FragmentKind.DocumentStart, null, null, 0, writer, fragments, cancellationToken);

        string? groupKey = null;
        for (var i = 0; i < request.Records.Count; i++)
        {
            var record = request.Records[i];
            var key = KeyOf(request, record);
            var startsGroup = request.GroupKey is not null && (i == 0 || !SameKey(key, groupKey));
            if (startsGroup)
            {
                groupKey = key;
                ComposeGroup(request, key, first: true, record.Ordinal, writer, fragments, cancellationToken);
            }

            Compose(request, TemplatePart.Row, FragmentKind.Row, record.Values, null, record.Ordinal, writer, fragments, cancellationToken);

            var endsGroup = request.GroupKey is not null
                && (i == request.Records.Count - 1 || !SameKey(KeyOf(request, request.Records[i + 1]), key));
            if (endsGroup)
            {
                ComposeGroup(request, key, first: false, record.Ordinal, writer, fragments, cancellationToken);
            }
        }

        Compose(request, TemplatePart.DocumentEnd, FragmentKind.DocumentEnd, null, null, 0, writer, fragments, cancellationToken);

        text.Flush();
        var html = stream.ToArray();
        if (html.LongLength != writer.BytesWritten)
        {
            throw new InvalidOperationException("The composed document and its byte count disagree.");
        }

        return new ComposedBlock(html, fragments);
    }

    private void ComposeGroup(
        CompositionRequest request,
        string? key,
        bool first,
        long ordinal,
        GuardedWriter writer,
        List<ComposedFragment> fragments,
        CancellationToken cancellationToken)
    {
        var part = first ? TemplatePart.GroupStart : TemplatePart.GroupEnd;
        var kind = first ? FragmentKind.GroupStart : FragmentKind.GroupEnd;
        if (!request.Template.Has(part))
        {
            return;
        }

        var group = RenderContextFactory.Group(key, first, continuesFromPreviousBlock: false, continuesInNextBlock: false);
        Compose(request, part, kind, null, group, ordinal, writer, fragments, cancellationToken);
    }

    private void Compose(
        CompositionRequest request,
        TemplatePart part,
        FragmentKind kind,
        IReadOnlyDictionary<string, object?>? item,
        IReadOnlyDictionary<string, object?>? group,
        long recordOrdinal,
        GuardedWriter writer,
        List<ComposedFragment> fragments,
        CancellationToken cancellationToken)
    {
        if (!request.Template.Has(part))
        {
            return;
        }

        var block = RenderContextFactory.Block(
            request.Block.Index,
            request.Block.RecordCount,
            request.Block.FirstRecordOrdinal,
            request.Block.IsFirst,
            request.Block.IsLast,
            kind == FragmentKind.Row ? recordOrdinal : null);
        var context = new RenderContext(
            request.Header,
            item,
            group,
            block,
            RenderContextFactory.NoAggregates,
            RenderContextFactory.Render(request.Render.Culture, request.Render.TimeZone, request.Render.Clock));

        var start = checked((int)writer.BytesWritten);
        adapter.RenderPart(request.Template, part, context, writer, cancellationToken);
        fragments.Add(new ComposedFragment(kind, start, checked((int)writer.BytesWritten) - start, kind == FragmentKind.Row ? recordOrdinal : 0));
    }

    private static string? KeyOf(CompositionRequest request, ComposedRecord record)
    {
        if (request.GroupKey is null)
        {
            return null;
        }

        return RenderContextFactory.TryResolve(record.Values, request.GroupKey, out var value)
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static bool SameKey(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
}
