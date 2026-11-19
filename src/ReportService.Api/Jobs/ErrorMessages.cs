using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ReportService.Api.Jobs;

/// <summary>
/// The sentence a consumer reads when its report failed for a reason this service can put in
/// words. A stored failure reason is the data step's own diagnostic - the code, the reason it
/// stopped at, the limit's token and the position - and the message is rendered from it, so
/// the answer names the limit that was actually applied to the data and never a number the
/// operator configured. The renderings are fixed text: a reason this build does not recognise
/// renders no message and is reported once, because a consumer is never handed a sentence the
/// service cannot stand behind.
/// </summary>
public static class ErrorMessages
{
    /// <summary>The code of a refusal because the report's data does not fit the limit applied to it.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>The sentence of a refusal the record count stopped: the record and the limit applied.</summary>
    private const string RecordsSentence =
        "The PDF report was not printed: record {0} exceeds this version's limit of {1} records per report (one block). Send a smaller report.";

    /// <summary>The sentence of a refusal the record's bytes stopped: the record and the byte limit applied.</summary>
    private const string BytesSentence =
        "The PDF report was not printed: record {0} exceeds this version's limit of {1} bytes of record data per report (one block). Send a smaller report.";

    // The stored reason is the data step's whole sentence: the code, then the reason it names
    // (the dimension that was crossed and the value applied), then the limit's own token and
    // the position the pass stopped at - in that order. A reason of another order, with a
    // missing key, with a value that is not a number or that names more than one block can
    // hold, is not one this build wrote.
    private const string ViolationPrefix = "the document cannot be used: code=";

    private const string CodeSeparator = " reason=";

    private const string DimensionKey = "dimension=";

    private const string AppliedKey = " applied=";

    /// <summary>The operator's configured value, which a reason may carry and no message ever shows.</summary>
    private const string ConfiguredKey = " configured=";

    private const string LimitKey = " limit=";

    private const string BlockLimit = "block";

    private const string OrdinalKey = " ordinal=";

    private const string OffsetKey = " offset=";

    /// <summary>The most records one block holds: a larger applied value cannot have been this version's.</summary>
    private const int BlockRecords = 2_000;

    /// <summary>The most bytes of record data one block holds: a larger applied value cannot have been this version's.</summary>
    private const long BlockRecordBytes = 1_048_576;

    /// <summary>
    /// Renders the message of a stored failure, or answers none when this build cannot word it.
    /// </summary>
    /// <param name="code">The code the job or its output failed with; null while it did not fail.</param>
    /// <param name="reason">The stored reason recorded beside the code; null when none was.</param>
    /// <param name="logger">Where a reason this build cannot read is reported, once per rendering.</param>
    /// <returns>The message a consumer reads, or null when the failure has none in this build.</returns>
    public static string? Format(string? code, string? reason, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (reason is null || !string.Equals(code, QuotaExceeded, StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryParseRefusal(reason, out var dimension, out var applied, out var ordinal))
        {
            logger.LogWarning(
                "A stored failure of {ErrorCode} carries a reason this build cannot put in words; the answer names no message for it",
                code);
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            dimension == BlockDimension.Records ? RecordsSentence : BytesSentence,
            ordinal,
            applied);
    }

    // The reason's own shape, read in its fixed order and read whole: a key out of order, an
    // absent one, trailing text, a value that is not a plain number or one no block can hold
    // all answer false.
    private static bool TryParseRefusal(string reason, out BlockDimension dimension, out long applied, out int ordinal)
    {
        dimension = BlockDimension.Records;
        applied = 0;
        ordinal = 0;

        if (!reason.StartsWith(ViolationPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = reason.AsSpan(ViolationPrefix.Length);

        // The code the sentence names is the code the job failed with.
        if (!TryTakeToSpace(ref rest, out var storedCode) || !storedCode.SequenceEqual(QuotaExceeded))
        {
            return false;
        }

        if (!TryTake(ref rest, CodeSeparator) || !TryTake(ref rest, DimensionKey))
        {
            return false;
        }

        if (!TryTakeToSpace(ref rest, out var dimensionText))
        {
            return false;
        }

        if (dimensionText.SequenceEqual("records"))
        {
            dimension = BlockDimension.Records;
        }
        else if (dimensionText.SequenceEqual("bytes"))
        {
            dimension = BlockDimension.Bytes;
        }
        else
        {
            return false;
        }

        if (!TryTake(ref rest, AppliedKey) || !TryTakeToSpace(ref rest, out var appliedText))
        {
            return false;
        }

        if (!long.TryParse(appliedText, NumberStyles.None, CultureInfo.InvariantCulture, out applied)
            || applied < 1
            || (dimension == BlockDimension.Records ? applied > BlockRecords : applied > BlockRecordBytes))
        {
            return false;
        }

        // The operator's configured value may stand beside the applied one; it is read and
        // dropped, because a consumer learns what was applied to its data and never what the
        // deployment configured.
        if (TryTake(ref rest, ConfiguredKey)
            && (!TryTakeToSpace(ref rest, out var configuredText)
                || !long.TryParse(configuredText, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        if (!TryTake(ref rest, LimitKey) || !TryTakeToSpace(ref rest, out var limitText) || !limitText.SequenceEqual(BlockLimit))
        {
            return false;
        }

        if (!TryTake(ref rest, OrdinalKey) || !TryTakeToSpace(ref rest, out var ordinalText))
        {
            return false;
        }

        if (!int.TryParse(ordinalText, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) || ordinal < 1)
        {
            return false;
        }

        if (!TryTake(ref rest, OffsetKey) || rest.IsEmpty)
        {
            return false;
        }

        // The offset closes the sentence: it is the position of the record in the uncompressed
        // document, a plain non-negative number, and nothing follows it.
        return long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryTake(ref ReadOnlySpan<char> rest, string literal)
    {
        if (!rest.StartsWith(literal, StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest[literal.Length..];
        return true;
    }

    private static bool TryTakeToSpace(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> value)
    {
        var space = rest.IndexOf(' ');
        if (space <= 0)
        {
            value = default;
            return false;
        }

        value = rest[..space];
        rest = rest[space..];
        return true;
    }

    /// <summary>Which of a block's two bounds a record crossed.</summary>
    private enum BlockDimension
    {
        /// <summary>The record count the pass applies.</summary>
        Records,

        /// <summary>The block's bound of record bytes.</summary>
        Bytes,
    }
}
