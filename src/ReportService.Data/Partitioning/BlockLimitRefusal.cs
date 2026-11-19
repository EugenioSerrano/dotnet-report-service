using System.Globalization;

namespace ReportService.Data;

/// <summary>Which of the block's two bounds a record would cross.</summary>
public enum BlockLimitDimension
{
    /// <summary>The record count the pass applies: the configured value, capped by the hard maximum.</summary>
    Records,

    /// <summary>The block's hard maximum of record bytes.</summary>
    Bytes,
}

/// <summary>
/// The refusal of the record that would take the block past its effective limit: the
/// dimension that was crossed, the value actually applied, and the record's position.
/// It never carries a value of the document, a storage key, a URL or a host path.
/// </summary>
public readonly record struct BlockLimitRefusal(
    BlockLimitDimension Dimension,
    long Applied,
    int Ordinal,
    long? Offset)
{
    /// <summary>The dimension and the value actually applied, as the reason carries them.</summary>
    public string Reason => Dimension == BlockLimitDimension.Records
        ? string.Create(CultureInfo.InvariantCulture, $"dimension=records applied={Applied}")
        : string.Create(CultureInfo.InvariantCulture, $"dimension=bytes applied={Applied}");

    /// <summary>The whole diagnostic: the limit, the dimension, the applied value and the position.</summary>
    /// <returns>The reason the refused snapshot ends with, and the operator's log line carries.</returns>
    public string Diagnostic()
    {
        var text = "limit=block " + Reason
            + string.Create(CultureInfo.InvariantCulture, $" ordinal={Ordinal}");
        return Offset is { } offset
            ? text + string.Create(CultureInfo.InvariantCulture, $" offset={offset}")
            : text;
    }
}
