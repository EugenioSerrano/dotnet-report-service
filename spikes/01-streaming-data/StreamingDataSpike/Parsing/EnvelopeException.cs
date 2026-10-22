using System.Globalization;

namespace StreamingDataSpike.Parsing;

/// <summary>The stable error codes reported by the reader and the validator.</summary>
public static class ErrorCodes
{
    /// <summary>The data does not follow the envelope (truncated, wrong types, duplicates, trailing content, totals).</summary>
    public const string DataInvalid = "DATA_INVALID";

    /// <summary>One record is larger than the record limit.</summary>
    public const string RecordTooLarge = "RECORD_TOO_LARGE";

    /// <summary>A size, depth, token or input quota was exceeded.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
}

/// <summary>
/// A data error with a stable code and a named limit or reason. The message
/// carries only these fields, the record ordinal and the byte offset: never
/// a file path and never a data value.
/// </summary>
public sealed class EnvelopeException : Exception
{
    private EnvelopeException(string code, string? limit, string? reason, long recordOrdinal, long byteOffset)
        : base(Describe(code, limit, reason, recordOrdinal, byteOffset))
    {
        Code = code;
        Limit = limit;
        Reason = reason;
        RecordOrdinal = recordOrdinal;
        ByteOffset = byteOffset;
    }

    /// <summary>One of <see cref="ErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>The name of the exceeded limit, or <see langword="null"/>.</summary>
    public string? Limit { get; }

    /// <summary>The name of the reason, or <see langword="null"/>.</summary>
    public string? Reason { get; }

    /// <summary>1-based ordinal of the record being read; 0 for the header or the envelope.</summary>
    public long RecordOrdinal { get; }

    /// <summary>Offset in the uncompressed input where the error was detected.</summary>
    public long ByteOffset { get; }

    /// <summary>A limit error.</summary>
    public static EnvelopeException ForLimit(string code, string limit, long recordOrdinal, long byteOffset) =>
        new(code, limit, null, recordOrdinal, byteOffset);

    /// <summary>A <see cref="ErrorCodes.DataInvalid"/> error with a reason.</summary>
    public static EnvelopeException Invalid(string reason, long recordOrdinal, long byteOffset) =>
        new(ErrorCodes.DataInvalid, null, reason, recordOrdinal, byteOffset);

    /// <summary>The single standard-error line of the command line.</summary>
    public string ToErrorLine() => Describe(Code, Limit, Reason, RecordOrdinal, ByteOffset);

    private static string Describe(string code, string? limit, string? reason, long recordOrdinal, long byteOffset)
    {
        var detail = limit is not null ? "limit=" + limit : "reason=" + reason;
        return string.Create(CultureInfo.InvariantCulture, $"error code={code} {detail} ordinal={recordOrdinal} offset={byteOffset}");
    }
}
