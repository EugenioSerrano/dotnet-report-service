using PdfSharp.Pdf;

namespace ReportService.Pdf.Assembly;

/// <summary>
/// The page-dictionary contract of one assembly: which entries the assembler may
/// change or add, and how every other entry of a source page is carried to the page
/// the assembly writes.
/// <para>
/// PDFsharp's page import copies only the entries it models, so an entry it does not
/// model - Chromium writes <c>/Tabs /S</c> on every page - is dropped on every
/// import: at the batch and again at the final merge. The entries are therefore
/// captured while the source part is read and re-inserted on the final document's
/// pages before its one save, which is the only place the value survives.
/// </para>
/// <para>
/// Only direct values are captured. A value that holds an indirect reference cannot
/// be reproduced in a rewritten document - its object number changes - so the entry
/// is left out and the verifier refuses the page as a missing entry: the check stays
/// fail-closed.
/// </para>
/// </summary>
internal static class PageDictionaryPolicy
{
    /// <summary>
    /// The entries the assembly may change, as the structural verification allows:
    /// the stamp's content stream and resources, the transparency group some writers
    /// add, the page box written to two decimals, and the page tree's own parent.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedChangedValues = new HashSet<string>(StringComparer.Ordinal)
    {
        "Contents",
        "Resources",
        "Group",
        "MediaBox",
        "CropBox",
        "Parent",
    };

    /// <summary>The entries the assembly may add, as the structural verification allows.</summary>
    public static readonly IReadOnlySet<string> AllowedAddedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Group",
        "MediaBox",
        "CropBox",
    };

    /// <summary>
    /// The source page's entries the assembly must preserve, or <see langword="null"/>
    /// when the page carries only entries the assembly may change or add.
    /// </summary>
    /// <param name="page">The source page, as the assembly read it.</param>
    public static IReadOnlyDictionary<string, PageEntryValue>? Capture(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Dictionary<string, PageEntryValue>? captured = null;
        foreach (var (key, item) in page.Elements)
        {
            if (AllowedChangedValues.Contains(key) || !PageEntryValue.TryOf(item, out var value))
            {
                continue;
            }

            captured ??= new Dictionary<string, PageEntryValue>(StringComparer.Ordinal);
            captured[key] = value;
        }

        return captured;
    }

    /// <summary>Re-inserts the captured entries on a page of the document about to be saved.</summary>
    /// <param name="document">The final document the page belongs to.</param>
    /// <param name="page">The page, as the final merge added it.</param>
    /// <param name="entries">The entries captured from the page's source, or <see langword="null"/>.</param>
    public static void Restore(
        PdfDocument document,
        PdfPage page,
        IReadOnlyDictionary<string, PageEntryValue>? entries)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        if (entries is null)
        {
            return;
        }

        foreach (var (key, value) in entries)
        {
            page.Elements[key] = value.ToItem(document);
        }
    }
}

/// <summary>One preserved page-dictionary entry's value, as a direct PDF item.</summary>
internal abstract record PageEntryValue
{
    /// <summary>Rebuilds the item inside <paramref name="document"/>.</summary>
    /// <param name="document">The document the value is written into.</param>
    public abstract PdfItem ToItem(PdfDocument document);

    /// <summary>Reads a PDFsharp item, or refuses one that cannot be reproduced faithfully.</summary>
    /// <param name="item">The source page dictionary's value.</param>
    /// <param name="value">The captured value.</param>
    public static bool TryOf(PdfItem? item, out PageEntryValue value)
    {
        switch (item)
        {
            case PdfName name:
                value = new NameValue(name.Value);
                return true;
            case PdfInteger integer:
                value = new IntegerValue(integer.Value);
                return true;
            case PdfReal real:
                value = new RealValue(real.Value);
                return true;
            case PdfBoolean boolean:
                value = new BooleanValue(boolean.Value);
                return true;
            case PdfString text:
                value = new TextValue(text.Value);
                return true;
            case PdfArray array:
                var items = new List<PageEntryValue>(array.Elements.Count);
                foreach (var element in array.Elements)
                {
                    if (!TryOf(element, out var itemValue))
                    {
                        value = null!;
                        return false;
                    }

                    items.Add(itemValue);
                }

                value = new ArrayValue(items);
                return true;
            case PdfDictionary dictionary when dictionary.Stream is null:
                var entries = new Dictionary<string, PageEntryValue>(StringComparer.Ordinal);
                foreach (var (key, element) in dictionary.Elements)
                {
                    if (!TryOf(element, out var entryValue))
                    {
                        value = null!;
                        return false;
                    }

                    entries[key] = entryValue;
                }

                value = new DictionaryValue(entries);
                return true;
            default:
                value = null!;
                return false;
        }
    }
}

/// <summary>A PDF name, such as the tab order's <c>/S</c>.</summary>
internal sealed record NameValue(string Value) : PageEntryValue
{
    /// <inheritdoc />
    public override PdfItem ToItem(PdfDocument document) => new PdfName(Value);
}

/// <summary>A PDF integer.</summary>
internal sealed record IntegerValue(int Value) : PageEntryValue
{
    /// <inheritdoc />
    public override PdfItem ToItem(PdfDocument document) => new PdfInteger(Value);
}

/// <summary>A PDF real number.</summary>
internal sealed record RealValue(double Value) : PageEntryValue
{
    /// <inheritdoc />
    public override PdfItem ToItem(PdfDocument document) => new PdfReal(Value);
}

/// <summary>A PDF boolean.</summary>
internal sealed record BooleanValue(bool Value) : PageEntryValue
{
    /// <inheritdoc />
    public override PdfItem ToItem(PdfDocument document) => new PdfBoolean(Value);
}

/// <summary>
/// A PDF string. PDFsharp writes the value as a literal string; a source string that
/// was hex-encoded is read back as a literal, so the verifier refuses the differing
/// canonical text - the fail-closed rule the assembly keeps for values it cannot
/// reproduce byte-for-byte.
/// </summary>
internal sealed record TextValue(string Value) : PageEntryValue
{
    /// <inheritdoc />
    public override PdfItem ToItem(PdfDocument document) => new PdfString(Value);
}

/// <summary>A PDF array of direct values.</summary>
internal sealed record ArrayValue(IReadOnlyList<PageEntryValue> Items) : PageEntryValue
{
    /// <inheritdoc />
    public override PdfItem ToItem(PdfDocument document)
    {
        var array = new PdfArray(document);
        foreach (var item in Items)
        {
            array.Elements.Add(item.ToItem(document));
        }

        return array;
    }
}

/// <summary>A PDF dictionary of direct values.</summary>
internal sealed record DictionaryValue(IReadOnlyDictionary<string, PageEntryValue> Entries) : PageEntryValue
{
    /// <inheritdoc />
    public override PdfItem ToItem(PdfDocument document)
    {
        var dictionary = new PdfDictionary(document);
        foreach (var (key, value) in Entries)
        {
            dictionary.Elements[key] = value.ToItem(document);
        }

        return dictionary;
    }
}
