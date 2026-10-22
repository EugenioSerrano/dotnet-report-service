using System.Globalization;
using System.Text.Json;
using MarkdownSpike.Input;

namespace MarkdownSpike.Rendering;

/// <summary>Receives each rendered part as soon as it is produced.</summary>
internal interface IPartSink
{
    /// <param name="kind">The part.</param>
    /// <param name="text">The rendered text of that part.</param>
    /// <param name="recordIndex">Zero-based index of the record the part belongs to (-1 for the document start, the record count for the document end).</param>
    void OnPart(PartKind kind, string text, long recordIndex);
}

/// <summary>
/// Walks the records with a one-record look-ahead and renders the parts in
/// document order: document start, then per record an optional group start,
/// the row and an optional group end, then the document end.
/// </summary>
internal static class PartSequencer
{
    public static (long Records, long Groups) Run(EnvelopeReader reader, PartRenderer renderer, IPartSink sink)
    {
        renderer.SetHeader(ScriptModel.ToScriptObject(reader.Header));
        sink.OnPart(PartKind.DocumentStart, renderer.Render(PartKind.DocumentStart), -1);

        long index = 0;
        long groups = 0;
        string? previousGroup = null;
        decimal groupAmount = 0m;

        using IEnumerator<JsonElement> items = reader.ReadItems().GetEnumerator();
        bool hasCurrent = items.MoveNext();
        JsonElement current = hasCurrent ? items.Current : default;
        while (hasCurrent)
        {
            bool hasNext = items.MoveNext();
            JsonElement next = hasNext ? items.Current : default;

            renderer.SetItem(ScriptModel.ToScriptObject(current));
            string? groupId = TextOf(current, "groupId");
            if (renderer.HasGroupParts && (index == 0 || groupId != previousGroup))
            {
                groups++;
                groupAmount = 0m;
                renderer.SetGroup(groupId ?? string.Empty, TextOf(current, "groupName") ?? string.Empty);
                sink.OnPart(PartKind.GroupStart, renderer.Render(PartKind.GroupStart), index);
            }

            sink.OnPart(PartKind.Row, renderer.Render(PartKind.Row), index);
            groupAmount += AmountOf(current);

            if (renderer.HasGroupParts && (!hasNext || TextOf(next, "groupId") != groupId))
            {
                renderer.SetGroupAmount(groupAmount);
                sink.OnPart(PartKind.GroupEnd, renderer.Render(PartKind.GroupEnd), index);
            }

            previousGroup = groupId;
            current = next;
            hasCurrent = hasNext;
            index++;
        }

        sink.OnPart(PartKind.DocumentEnd, renderer.Render(PartKind.DocumentEnd), index);
        return (index, groups);
    }

    private static string? TextOf(JsonElement record, string name) =>
        record.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal AmountOf(JsonElement record) =>
        record.TryGetProperty("amount", out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? decimal.Parse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0m;
}
