namespace XlsxWriterSpike.Typing;

/// <summary>A failure of the experiment itself (bad input, type mismatch): exit code 1.</summary>
internal class SpikeException(string message) : Exception(message);

/// <summary>Invalid command-line arguments: exit code 2.</summary>
internal sealed class UsageException(string message) : SpikeException(message);

/// <summary>
/// A value or a row count that the spreadsheet cannot hold exactly: the export
/// fails with <c>OUTPUT_LIMIT_EXCEEDED</c> instead of rounding or truncating.
/// Exit code 3.
/// </summary>
internal sealed class OutputLimitExceededException : Exception
{
    /// <summary>Creates the error.</summary>
    /// <param name="reason"><c>precision</c>, <c>cell-length</c> or <c>rows</c>.</param>
    /// <param name="row">1-based record number.</param>
    /// <param name="column">Column name, or <c>-</c> for the row limit.</param>
    /// <param name="value">The offending raw value (or the row count).</param>
    /// <param name="beforeWriting"><c>true</c> when raised before any output byte was written.</param>
    public OutputLimitExceededException(string reason, long row, string column, string value, bool beforeWriting = false)
        : base($"error code={ErrorCode} reason={reason} row={row} column={column} value={Shorten(value)}")
    {
        Reason = reason;
        Row = row;
        Column = column;
        Value = value;
        BeforeWriting = beforeWriting;
    }

    /// <summary>The error code.</summary>
    public const string ErrorCode = "OUTPUT_LIMIT_EXCEEDED";

    /// <summary>Why the limit was exceeded.</summary>
    public string Reason { get; }

    /// <summary>1-based record number.</summary>
    public long Row { get; }

    /// <summary>Column name.</summary>
    public string Column { get; }

    /// <summary>Offending raw value.</summary>
    public string Value { get; }

    /// <summary>Raised before any output byte was written.</summary>
    public bool BeforeWriting { get; }

    private static string Shorten(string value) =>
        value.Length <= 60 ? value : string.Concat(value.AsSpan(0, 40), $"...(length {value.Length})");
}
