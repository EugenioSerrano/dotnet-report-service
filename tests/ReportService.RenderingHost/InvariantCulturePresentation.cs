using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReportService.Rendering;
using ReportService.Templates;

namespace ReportService.RenderingHost;

/// <summary>
/// The known-bad control that presents every formatted value in the invariant culture instead
/// of the job's: the record count the header prints as it is, each row's date and amount, and
/// the acceptance instant the document's close prints. It acts on the composition before the
/// expected text is extracted, the way the other composition controls do, so the product's own
/// checks read the same wrong text they would print.
/// The header prints the record count twice - in the customer line and in the records
/// paragraph - and the anchor check passes when the anchored value appears anywhere in the
/// document parts. A single occurrence left in the job's culture would satisfy that check, so
/// the control presents every occurrence and refuses to run when the presentation changes no
/// text or when a raw occurrence survives in either document part: a third print added to a
/// template later fails it loudly instead of passing it silently.
/// The judge, which computes its expectation from the snapshot and the package's declared
/// culture and zone, refuses the document on its own.
/// </summary>
internal sealed partial class InvariantCulturePresentation(
    IReadOnlyList<ComposedRecord> records,
    IReadOnlyDictionary<string, object?> header,
    string timeZone,
    DateTimeOffset clock) : IComposedBlockMutation
{
    /// <summary>The name the rendering suite selects this control by.</summary>
    public const string ControlName = "PR-CULTURE";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private const string CalendarFormat = "yyyy-MM-dd";

    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);

    /// <inheritdoc />
    public string Name => ControlName;

    /// <inheritdoc />
    public ComposedBlock Apply(ComposedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var byOrdinal = records.ToDictionary(record => record.Ordinal);
        using var stream = new MemoryStream(block.Html.Length);
        var fragments = new List<ComposedFragment>(block.Fragments.Count);
        foreach (var fragment in block.Fragments)
        {
            var text = fragment.Kind switch
            {
                FragmentKind.DocumentStart => CountText(block.TextOf(fragment)),
                FragmentKind.Row => RowText(block.TextOf(fragment), byOrdinal[fragment.RecordOrdinal]),
                FragmentKind.DocumentEnd => ClockText(block.TextOf(fragment)),
                _ => block.TextOf(fragment),
            };
            var bytes = Encoding.UTF8.GetBytes(text);
            fragments.Add(fragment with { Start = checked((int)stream.Length), Length = bytes.Length });
            stream.Write(bytes);
        }

        var mutated = new ComposedBlock(stream.ToArray(), fragments);
        GuardNoRawCount(mutated);
        return mutated;
    }

    // The header prints the record count as it is: the control presents every occurrence of it
    // as a number in the invariant culture. The premise is the presentation itself - a count
    // whose invariant text is the raw token would leave the printed text unchanged, so the
    // control could not discriminate at all - and the result is guarded over the whole
    // composed block once the rewrite is done.
    private string CountText(string text)
    {
        var raw = Member(header, "totals", "recordCount");
        var presented = decimal.TryParse(raw, NumberStyles.Number, Invariant, out var number)
            ? number.ToString("N0", Invariant)
            : raw;
        if (string.Equals(presented, raw, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The control cannot discriminate: the record count '{raw}' is presented as '{presented}' in the invariant culture, the same text, so presenting it changes nothing.");
        }

        text = CustomerLine().Replace(text, match => match.Groups[1].Value + presented + match.Groups[3].Value, count: 1);
        return RecordsLine().Replace(text, match => match.Groups[1].Value + presented + match.Groups[3].Value, count: 1);
    }

    // The anchor check reads the text of the document parts and passes when the anchored value
    // appears anywhere in it, so one surviving occurrence would satisfy the check whatever the
    // control presents. The control therefore refuses to exist in that state: a third print
    // added to a template later fails here, loudly, instead of being silently satisfied.
    private void GuardNoRawCount(ComposedBlock block)
    {
        var raw = Member(header, "totals", "recordCount");
        var inStart = Occurrences(block, raw, FragmentKind.DocumentStart);
        var inEnd = Occurrences(block, raw, FragmentKind.DocumentEnd);
        if (inStart + inEnd > 0)
        {
            throw new InvalidDataException(
                $"The control presents the record count '{raw}' in the invariant culture, but the raw text survives in the document parts (document start: {inStart}, document end: {inEnd}); the anchor check would accept the document through it.");
        }
    }

    private static int Occurrences(ComposedBlock block, string raw, FragmentKind kind)
    {
        if (raw.Length == 0)
        {
            return 0;
        }

        var occurrences = 0;
        foreach (var fragment in block.Fragments)
        {
            if (fragment.Kind != kind)
            {
                continue;
            }

            var text = block.TextOf(fragment);
            for (var index = text.IndexOf(raw, StringComparison.Ordinal);
                index >= 0;
                index = text.IndexOf(raw, index + raw.Length, StringComparison.Ordinal))
            {
                occurrences++;
            }
        }

        return occurrences;
    }

    private string RowText(string html, ComposedRecord record)
    {
        var date = DateText(record, "date");
        var rawAmount = Member(record, "amount");
        var amount = decimal.TryParse(rawAmount, NumberStyles.Number, Invariant, out var number)
            ? number.ToString("N2", Invariant)
            : rawAmount;
        return AmountCell().Replace(
            DateCell().Replace(html, match => match.Groups[1].Value + date + match.Groups[3].Value, count: 1),
            match => match.Groups[1].Value + amount + match.Groups[3].Value,
            count: 1);
    }

    private string ClockText(string text)
    {
        var instant = TimeZoneInfo.ConvertTime(clock, zone).ToString("dd/MM/yyyy HH:mm", Invariant);
        return ClockLine().Replace(text, match => match.Groups[1].Value + instant + match.Groups[3].Value, count: 1);
    }

    // A date-only value is a calendar date and is never converted; an instant is shown in the
    // package's zone. Both are then written in the invariant culture.
    private string DateText(ComposedRecord record, string member)
    {
        var raw = Member(record, member);
        if (DateOnly.TryParseExact(raw, CalendarFormat, Invariant, DateTimeStyles.None, out var calendar))
        {
            return calendar.ToString("dd/MM/yyyy", Invariant);
        }

        return DateTimeOffset.TryParse(raw, Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? TimeZoneInfo.ConvertTime(instant, zone).ToString("dd/MM/yyyy", Invariant)
            : raw;
    }

    private static string Member(IReadOnlyDictionary<string, object?> source, string member) =>
        source.TryGetValue(member, out var value) && value is string text
            ? text
            : throw new InvalidDataException($"The control reads no text at '{member}'.");

    private static string Member(IReadOnlyDictionary<string, object?> source, string parent, string member) =>
        source.TryGetValue(parent, out var value) && value is IReadOnlyDictionary<string, object?> nested
            ? Member(nested, member)
            : throw new InvalidDataException($"The control reads no text at '{parent}.{member}'.");

    private static string Member(ComposedRecord record, string member) => Member(record.Values, member);

    [GeneratedRegex("(<p class=\"customer\">[^<]* &middot; [^<]* &middot; )([^<]*)(</p>)", RegexOptions.CultureInvariant)]
    private static partial Regex CustomerLine();

    [GeneratedRegex("(<p class=\"records\">Records: )([^<]*)(</p>)", RegexOptions.CultureInvariant)]
    private static partial Regex RecordsLine();

    [GeneratedRegex("(<td class=\"col-date\">)([^<]*)(</td>)", RegexOptions.CultureInvariant)]
    private static partial Regex DateCell();

    [GeneratedRegex("(<td class=\"col-amount\">)([^<]*)(</td>)", RegexOptions.CultureInvariant)]
    private static partial Regex AmountCell();

    [GeneratedRegex("(<footer><p class=\"clock\">)([^<]*)(</p></footer>)", RegexOptions.CultureInvariant)]
    private static partial Regex ClockLine();
}
