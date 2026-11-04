using System.Globalization;

namespace ReportService.Data;

/// <summary>
/// A snapshot that cannot be used, named by the code, the reason or limit and the
/// position the pass stopped at. It never carries a value of the document, a storage
/// key or a host path: a diagnostic says which rule was broken, where, and nothing
/// about the content that broke it.
/// </summary>
public sealed class DataContractViolation : Exception
{
    /// <summary>The document is not a usable document of the profile.</summary>
    public const string DataInvalid = "DATA_INVALID";

    /// <summary>One record occupies more bytes than a record may.</summary>
    public const string RecordTooLarge = "RECORD_TOO_LARGE";

    /// <summary>A limit of the reader or of the partitioner was reached.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>The code of the condition that stopped the pass.</summary>
    public string Code { get; }

    /// <summary>Why the document is invalid, when the code is <see cref="DataInvalid"/>.</summary>
    public string? Reason { get; }

    /// <summary>Which limit was reached, when the code names a quota.</summary>
    public string? Limit { get; }

    /// <summary>The ordinal of the record the pass stopped at; 0 is the header, 1 the first record.</summary>
    public int Ordinal { get; }

    /// <summary>The offset, in bytes of the uncompressed document, the pass stopped at.</summary>
    public long Offset { get; }

    /// <summary>The field's path relative to the value, when a check of the template's schema refused it.</summary>
    public string? Path { get; }

    /// <summary>The rule of the template's schema that refused the value, when one did.</summary>
    public string? Keyword { get; }

    /// <summary>Describes a refusal of the document.</summary>
    /// <param name="code">One of <see cref="DataInvalid"/>, <see cref="RecordTooLarge"/> or <see cref="QuotaExceeded"/>.</param>
    /// <param name="reason">Why it is invalid, or <see langword="null"/> when the code names no reason.</param>
    /// <param name="limit">The limit that was reached, or <see langword="null"/> when no limit was.</param>
    /// <param name="ordinal">The ordinal of the record the pass stopped at.</param>
    /// <param name="offset">The offset of the uncompressed document the pass stopped at.</param>
    /// <param name="path">The field's path relative to the value, when a check of the template's schema refused it.</param>
    /// <param name="keyword">The rule of the template's schema that refused the value, when one did.</param>
    public DataContractViolation(
        string code,
        string? reason,
        string? limit,
        int ordinal,
        long offset,
        string? path = null,
        string? keyword = null)
        : base(Describe(code, reason, limit, ordinal, offset, path, keyword))
    {
        Code = code;
        Reason = reason;
        Limit = limit;
        Ordinal = ordinal;
        Offset = offset;
        Path = path;
        Keyword = keyword;
    }

    private static string Describe(string code, string? reason, string? limit, int ordinal, long offset, string? path, string? keyword)
    {
        var text = $"the document cannot be used: code={code}";
        if (reason is not null)
        {
            text += " reason=" + reason;
        }

        if (limit is not null)
        {
            text += " limit=" + limit;
        }

        text = text
            + " ordinal=" + ordinal.ToString(CultureInfo.InvariantCulture)
            + " offset=" + offset.ToString(CultureInfo.InvariantCulture);

        // A value the template's schema refused names the field's path and the rule, and
        // nothing else: never the value the producer wrote.
        if (path is not null)
        {
            text += " path=" + path;
        }

        if (keyword is not null)
        {
            text += " keyword=" + keyword;
        }

        return text;
    }
}
