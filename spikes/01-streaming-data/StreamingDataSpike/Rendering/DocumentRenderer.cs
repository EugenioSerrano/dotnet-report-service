using System.Diagnostics;
using StreamingDataSpike.Measuring;
using StreamingDataSpike.Parsing;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// Renders a whole document, one record at a time, through one or more
/// engines into hashing sinks (nothing is written to disk). With more than
/// one engine every part of every record is rendered by each engine into a
/// string, the strings are compared and then appended to each engine's sink.
/// </summary>
/// <remarks>
/// Order: <c>document-start</c>; for each record, when its <c>groupId</c>
/// differs from the previous one, <c>group-end</c> of the previous group (if
/// any) and <c>group-start</c>; then <c>row</c>; at the end the last
/// <c>group-end</c> and <c>document-end</c>. Templates without group parts
/// render only the document parts and the rows. <c>aggregates.groupAmount</c>
/// is <c>0.00</c> at <c>group-start</c>, includes the current record at
/// <c>row</c> and is the group total at <c>group-end</c>.
/// </remarks>
internal sealed class DocumentRenderer(IReadOnlyList<IRecordRenderer> renderers, bool hasGroups, bool perBlock)
{
    /// <summary>Called for every rendered row with the record and the row's HTML (compare mode only).</summary>
    public Action<EnvelopeRecord, string>? RowObserver { get; init; }

    /// <summary>Renders <paramref name="file"/> and returns the hashes.</summary>
    public async Task<DocumentOutcome> RenderAsync(string file, EnvelopeLimits limits, Meter? meter, CancellationToken cancellationToken)
    {
        if (perBlock && renderers.Count > 1)
        {
            throw new InvalidOperationException("The per-block context is measured one engine at a time.");
        }

        var run = new Run(renderers, perBlock, RowObserver);
        await using var input = InputFiles.Open(file);
        var reader = new EnvelopeReader(input, limits);
        var header = await reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        var validator = new EnvelopeValidator(header, limits);
        var headerValues = TemplateValues.FromModel(header.Values);
        var renderValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["culture"] = "es-AR",
            ["timeZone"] = "America/Argentina/Buenos_Aires",
            ["generatedAt"] = headerValues.GetValueOrDefault("generatedAt"),
        };

        run.NewContext(headerValues, renderValues);
        run.RenderPart("document-start", null);

        long blockIndex = 0;
        long blockRecords = 0;
        long blockBytes = 0;
        string? currentGroup = null;
        var groupOpen = false;
        var groupAmount = 0.00m;
        Dictionary<string, object?> group = NewGroup(null, null);

        await foreach (var record in reader.ReadItemsAsync(cancellationToken).ConfigureAwait(false))
        {
            validator.Observe(record);
            meter?.OnRecord();

            if (blockIndex == 0 || blockRecords >= limits.BlockRecords || blockBytes + record.ByteLength > limits.BlockBytes)
            {
                blockIndex++;
                blockRecords = 0;
                blockBytes = 0;
                if (perBlock)
                {
                    run.NewContext(headerValues, renderValues);
                }
            }

            blockRecords++;
            blockBytes += record.ByteLength;

            var groupId = record.Values.GetValueOrDefault("groupId") as string;
            if (hasGroups && (!groupOpen || !string.Equals(groupId, currentGroup, StringComparison.Ordinal)))
            {
                if (groupOpen)
                {
                    run.Context.Group = group;
                    run.Context.Aggregates = NewAggregates(groupAmount);
                    run.RenderPart("group-end", null);
                }

                currentGroup = groupId;
                groupOpen = true;
                groupAmount = 0.00m;
                group = NewGroup(groupId, record.Values.GetValueOrDefault("groupName") as string);
                run.Context.Group = group;
                run.Context.Aggregates = NewAggregates(groupAmount);
                run.RenderPart("group-start", null);
            }

            if (record.Values.GetValueOrDefault("amount") is decimal amount)
            {
                groupAmount += amount;
            }

            run.Context.Item = TemplateValues.FromModel(record.Values);
            run.Context.Group = group;
            run.Context.Block = new Dictionary<string, object?>(2, StringComparer.Ordinal)
            {
                ["index"] = TemplateValues.Text(blockIndex),
                ["recordCount"] = TemplateValues.Text(blockRecords),
            };
            run.Context.Aggregates = NewAggregates(groupAmount);
            run.RenderPart("row", record);
        }

        validator.Complete(reader.InputBytes);
        run.Context.Item = [];
        if (groupOpen)
        {
            run.Context.Group = group;
            run.Context.Aggregates = NewAggregates(groupAmount);
            run.RenderPart("group-end", null);
        }

        run.RenderPart("document-end", null);
        return run.Finish(validator, reader);
    }

    private static Dictionary<string, object?> NewGroup(string? groupId, string? groupName) =>
        new(2, StringComparer.Ordinal) { ["groupId"] = groupId, ["groupName"] = groupName };

    private static Dictionary<string, object?> NewAggregates(decimal groupAmount) =>
        new(1, StringComparer.Ordinal) { ["groupAmount"] = TemplateValues.Text(groupAmount) };

    /// <summary>The mutable state of one document render.</summary>
    private sealed class Run(IReadOnlyList<IRecordRenderer> renderers, bool perBlock, Action<EnvelopeRecord, string>? rowObserver)
    {
        private readonly HashingTextWriter[] _sinks = [.. renderers.Select(_ => new HashingTextWriter())];
        private readonly StringWriter[]? _parts = renderers.Count > 1 ? [.. renderers.Select(_ => new StringWriter())] : null;
        private readonly long[] _ticks = new long[renderers.Count];
        private long _mismatches;
        private string? _firstMismatch;

        public RenderContext Context { get; private set; } = null!;

        public void NewContext(Dictionary<string, object?> header, Dictionary<string, object?> render) =>
            Context = new RenderContext { Header = header, Render = render, ReuseEngineState = perBlock };

        public void RenderPart(string part, EnvelopeRecord? record)
        {
            if (!perBlock)
            {
                // A fresh context for every render: only the values are carried over.
                Context = new RenderContext
                {
                    Header = Context.Header,
                    Render = Context.Render,
                    Item = Context.Item,
                    Group = Context.Group,
                    Block = Context.Block,
                    Aggregates = Context.Aggregates,
                };
            }

            for (var i = 0; i < renderers.Count; i++)
            {
                TextWriter target = _parts is null ? _sinks[i] : _parts[i];
                if (_parts is not null)
                {
                    _parts[i].GetStringBuilder().Clear();
                }

                var started = Stopwatch.GetTimestamp();
                renderers[i].Render(part, Context, target);
                _ticks[i] += Stopwatch.GetTimestamp() - started;
            }

            if (_parts is null)
            {
                return;
            }

            var first = _parts[0].ToString();
            for (var i = 0; i < renderers.Count; i++)
            {
                var text = i == 0 ? first : _parts[i].ToString();
                if (i > 0 && !string.Equals(text, first, StringComparison.Ordinal))
                {
                    _mismatches++;
                    _firstMismatch ??= $"record {record?.Ordinal ?? 0}, part {part}, {renderers[0].Engine} vs {renderers[i].Engine}: [{first}] vs [{text}]";
                }

                _sinks[i].Write(text);
            }

            if (record is not null && part == "row")
            {
                rowObserver?.Invoke(record, first);
            }
        }

        public DocumentOutcome Finish(EnvelopeValidator validator, EnvelopeReader reader)
        {
            var results = new List<EngineOutcome>(renderers.Count);
            for (var i = 0; i < renderers.Count; i++)
            {
                var hash = _sinks[i].Finish();
                results.Add(new EngineOutcome(renderers[i].Engine, hash, _sinks[i].Bytes, Stopwatch.GetElapsedTime(0, _ticks[i]).TotalSeconds));
                _sinks[i].Dispose();
            }

            return new DocumentOutcome(validator.Records, validator.GroupRuns, validator.Blocks, reader.MaxBufferBytes, reader.MaxRecordBytes, results, _mismatches, _firstMismatch);
        }
    }
}

/// <summary>One engine's document.</summary>
internal sealed record EngineOutcome(string Engine, string Sha256, long Bytes, double RenderSeconds);

/// <summary>The result of one document render.</summary>
internal sealed record DocumentOutcome(
    long Records,
    long Groups,
    long Blocks,
    int MaxBufferBytes,
    long MaxRecordBytes,
    IReadOnlyList<EngineOutcome> Engines,
    long Mismatches,
    string? FirstMismatch);
