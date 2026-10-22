using System.Text.Json;

namespace XlsxWriterSpike.Input;

/// <summary>One raw value of a record: the JSON token kind and its text.</summary>
internal struct RawValue
{
    /// <summary>Token kind (<see cref="JsonTokenType.None"/> when the property was absent).</summary>
    public JsonTokenType Token;

    /// <summary>Unescaped string, or the raw number text exactly as in the file.</summary>
    public string? Text;
}

/// <summary>One record of the <c>items</c> array, by profile column; reused between records.</summary>
internal sealed class RawRecord(int columnCount)
{
    /// <summary>Values in profile column order.</summary>
    public RawValue[] Values { get; } = new RawValue[columnCount];

    /// <summary>1-based record number in the source.</summary>
    public long RecordNumber { get; set; }

    /// <summary>Clears every value before the next record is read.</summary>
    public void Reset() => Array.Clear(Values);
}
