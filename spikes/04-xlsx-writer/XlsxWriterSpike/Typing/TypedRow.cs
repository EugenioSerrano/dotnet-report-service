namespace XlsxWriterSpike.Typing;

/// <summary>What a typed cell holds.</summary>
internal enum CellKind
{
    /// <summary>No cell is written.</summary>
    Empty,

    /// <summary>Numeric cell.</summary>
    Number,

    /// <summary>Text cell.</summary>
    Text,
}

/// <summary>One typed cell.</summary>
internal struct TypedCell
{
    /// <summary>What the cell holds.</summary>
    public CellKind Kind;

    /// <summary>The number, when <see cref="Kind"/> is <see cref="CellKind.Number"/>.</summary>
    public decimal Number;

    /// <summary>Text of the cell: the text itself, or the invariant number text written to the file.</summary>
    public string? Text;
}

/// <summary>One typed row; the same instance is reused for every record.</summary>
internal sealed class TypedRow(int columnCount)
{
    /// <summary>Cells in profile order.</summary>
    public TypedCell[] Cells { get; } = new TypedCell[columnCount];

    /// <summary>1-based record number in the source.</summary>
    public long RecordNumber { get; set; }
}
