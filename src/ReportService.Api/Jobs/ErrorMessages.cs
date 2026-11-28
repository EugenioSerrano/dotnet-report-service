using System.Globalization;
using Microsoft.Extensions.Logging;
using ReportService.Jobs;

namespace ReportService.Api.Jobs;

/// <summary>
/// The sentence a consumer reads when its report failed for a reason this service can put in
/// words. A stored failure reason is the diagnostic the step recorded - the code, the reason it
/// stopped at and the position - and the message is rendered from it. The renderings are fixed
/// text: a reason this build does not recognise renders no message and is reported once,
/// because a consumer is never handed a sentence the service cannot stand behind.
/// </summary>
public static class ErrorMessages
{
    /// <summary>The code of a refusal because one text field of a detail record is larger than the PDF output admits.</summary>
    public const string RowBoundExceeded = "ROW_BOUND_EXCEEDED";

    /// <summary>The code an output ends with when a retryable cause exhausted the attempts it was allowed.</summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>The code an output ends with when the last attempt's recorded cause is a deadline.</summary>
    public const string RenderTimeout = "RENDER_TIMEOUT";

    /// <summary>The code an output ends with when an image source is not an image data URI it admits.</summary>
    public const string ImageSourceInvalid = "IMAGE_SOURCE_INVALID";

    /// <summary>The sentence of an image-source refusal: the part, the record clause and the attribute.</summary>
    private const string ImageSourceSentence =
        "The {0} part{1} writes an image whose {2} is not an image this output accepts. Give the image as a base64 data URI of a PNG, JPEG, GIF, WebP or SVG image; a remote reference is never printed, and a relative one is not printed in a PDF.";

    /// <summary>The code an output ends with when a value is too large for its format.</summary>
    public const string OutputLimitExceeded = "OUTPUT_LIMIT_EXCEEDED";

    /// <summary>The sentence of an image too large for the HTML output: the part, the record clause, the attribute, the size and the bound.</summary>
    private const string ImageBytesSentence =
        "The {0} part{1} writes an image whose {2} holds {3} bytes; the HTML output accepts at most {4} bytes per image. Use a smaller image.";

    private static readonly string[] ImageSourceParts = ["document-start", "group-start", "row", "group-end", "document-end"];

    /// <summary>The sentence of a refusal the row bound stopped: the record, the field, its size and the bound.</summary>
    private const string RowBoundSentence =
        "Record {0} has a text field at {1} of {2} bytes of UTF-8; the PDF output accepts at most {3} bytes per field. Shorten that field.";

    /// <summary>The sentence of an output whose retryable attempts were exhausted with no deadline cause.</summary>
    private const string ExecutionFailedSentence =
        "The service could not complete this report. Your data and template are not the cause; the request can be submitted again later.";

    /// <summary>The sentence of an output whose last recorded cause was a deadline.</summary>
    private const string RenderTimeoutSentence =
        "This report took too long to print. Your data and template are not the cause; the request can be submitted again later.";

    // The stored reason is the data step's whole sentence: the code, then the reason it names
    // (the rule that refused the value), then the limit's own token and the position the pass
    // stopped at - in that order. A reason of another order, with a missing key, with a value
    // that is not a number or that names another limit, is not one this build wrote.
    private const string ViolationPrefix = "the document cannot be used: code=";

    private const string CodeSeparator = " reason=";

    /// <summary>The reason word of the row bound's refusal.</summary>
    private const string FieldTooLarge = "field-too-large";

    /// <summary>The limit token of the row bound's refusal.</summary>
    private const string RowBoundLimit = "row-bound";

    private const string SizeKey = " size=";

    private const string LimitKey = " limit=";

    private const string BoundKey = " bound=";

    private const string PathKey = " path=";

    /// <summary>The most bytes of UTF-8 one text field of a detail record may carry: a larger bound is not this version's.</summary>
    private const int FieldBoundBytes = 262_144;

    private const string OrdinalKey = " ordinal=";

    private const string OffsetKey = " offset=";

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

        if (reason is null)
        {
            return null;
        }

        if (string.Equals(code, OutputLimitExceeded, StringComparison.Ordinal)
            && reason.StartsWith("reason=image-bytes ", StringComparison.Ordinal))
        {
            if (!TryParseImageBytes(reason, out var part, out var ordinal, out var attribute, out var size, out var bound))
            {
                WarnUnreadable(OutputLimitExceeded, logger);
                return null;
            }

            var record = ordinal > 0
                ? string.Format(CultureInfo.InvariantCulture, " of record {0}", ordinal)
                : string.Empty;
            return string.Format(CultureInfo.InvariantCulture, ImageBytesSentence, part, record, attribute, size, bound);
        }

        if (string.Equals(code, ImageSourceInvalid, StringComparison.Ordinal))
        {
            if (!TryParseImageSource(reason, out var part, out var ordinal, out var attribute))
            {
                WarnUnreadable(ImageSourceInvalid, logger);
                return null;
            }

            var record = ordinal > 0
                ? string.Format(CultureInfo.InvariantCulture, " of record {0}", ordinal)
                : string.Empty;
            return string.Format(CultureInfo.InvariantCulture, ImageSourceSentence, part, record, attribute);
        }

        if (string.Equals(code, RowBoundExceeded, StringComparison.Ordinal))
        {
            if (!TryParseRowBound(reason, out var ordinal, out var size, out var bound, out var path))
            {
                WarnUnreadable(RowBoundExceeded, logger);
                return null;
            }

            return string.Format(CultureInfo.InvariantCulture, RowBoundSentence, ordinal, path, size, bound);
        }

        // An exhausted retry budget names one of the three tokens this service writes, and its
        // sentence is the fixed text of its code: never built from the exception, never naming a
        // path, a key, a host or an attempt's values.
        if (string.Equals(code, ExecutionFailed, StringComparison.Ordinal)
            || string.Equals(code, RenderTimeout, StringComparison.Ordinal))
        {
            if (!AttemptCauseVocabulary.IsExhaustionReason(reason))
            {
                WarnUnreadable(code!, logger);
                return null;
            }

            return string.Equals(code, ExecutionFailed, StringComparison.Ordinal)
                ? ExecutionFailedSentence
                : RenderTimeoutSentence;
        }

        return null;
    }

    // The image-bytes reason, read whole and in order:
    // "reason=image-bytes part=<part> ordinal=<n> attribute=<name> size=<bytes> bound=<bytes>".
    private static bool TryParseImageBytes(
        string reason, out string part, out long ordinal, out string attribute, out long size, out long bound)
    {
        part = string.Empty;
        attribute = string.Empty;
        ordinal = size = bound = 0;
        var fields = reason.Split(' ');
        if (fields.Length != 6 || fields[0] != "reason=image-bytes")
        {
            return false;
        }

        if (!TryParseImageSource(string.Join(' ', fields[1], fields[2], fields[3]), out part, out ordinal, out attribute)
            || !fields[4].StartsWith("size=", StringComparison.Ordinal)
            || !fields[5].StartsWith("bound=", StringComparison.Ordinal)
            || !long.TryParse(fields[4]["size=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out size)
            || !long.TryParse(fields[5]["bound=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out bound))
        {
            return false;
        }

        return true;
    }

    // The image-source reason, read whole and in order: "part=<part> ordinal=<n> attribute=<name>".
    // The part is one of the five part tokens, the ordinal a non-negative number, and the
    // attribute the name of an image source; anything else is not a reason this build wrote.
    private static bool TryParseImageSource(string reason, out string part, out long ordinal, out string attribute)
    {
        part = string.Empty;
        ordinal = 0;
        attribute = string.Empty;
        var fields = reason.Split(' ');
        if (fields.Length != 3
            || !fields[0].StartsWith("part=", StringComparison.Ordinal)
            || !fields[1].StartsWith("ordinal=", StringComparison.Ordinal)
            || !fields[2].StartsWith("attribute=", StringComparison.Ordinal))
        {
            return false;
        }

        var partText = fields[0]["part=".Length..];
        var attributeText = fields[2]["attribute=".Length..];
        if (Array.IndexOf(ImageSourceParts, partText) < 0
            || !long.TryParse(fields[1]["ordinal=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || !(attributeText == "src" || attributeText == "href" || attributeText.EndsWith(":href", StringComparison.Ordinal))
            || attributeText.Length > 64)
        {
            return false;
        }

        part = partText;
        ordinal = number;
        attribute = attributeText;
        return true;
    }

    // A reason this build cannot read is reported once, naming the code and never the text a
    // consumer could not use anyway.
    private static void WarnUnreadable(string code, ILogger logger) =>
        logger.LogWarning(
            "A stored failure of {ErrorCode} carries a reason this build cannot put in words; the answer names no message for it",
            code);

    // The row bound's reason, read in the delivered key order and read whole: the reason word,
    // the field's size and the bound it crossed, the limit's token, the record and its start,
    // and the field's path closing the sentence. A key out of order, an absent one, a size that
    // is not above the bound, a bound this version does not apply, a negative position or a
    // path the renderer could not have written all answer false.
    private static bool TryParseRowBound(
        string reason,
        out int ordinal,
        out long size,
        out long bound,
        out string path)
    {
        ordinal = 0;
        size = 0;
        bound = 0;
        path = string.Empty;

        if (!reason.StartsWith(ViolationPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = reason.AsSpan(ViolationPrefix.Length);

        if (!TryTakeToSpace(ref rest, out var storedCode) || !storedCode.SequenceEqual(RowBoundExceeded))
        {
            return false;
        }

        if (!TryTake(ref rest, CodeSeparator)
            || !TryTake(ref rest, FieldTooLarge)
            || !TryTake(ref rest, SizeKey)
            || !TryTakeToSpace(ref rest, out var sizeText)
            || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out size)
            || size < 1)
        {
            return false;
        }

        if (!TryTake(ref rest, BoundKey)
            || !TryTakeToSpace(ref rest, out var boundText)
            || !long.TryParse(boundText, NumberStyles.None, CultureInfo.InvariantCulture, out bound)
            || bound != FieldBoundBytes
            || size <= bound)
        {
            return false;
        }

        if (!TryTake(ref rest, LimitKey)
            || !TryTakeToSpace(ref rest, out var limitText)
            || !limitText.SequenceEqual(RowBoundLimit))
        {
            return false;
        }

        if (!TryTake(ref rest, OrdinalKey)
            || !TryTakeToSpace(ref rest, out var ordinalText)
            || !int.TryParse(ordinalText, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
            || ordinal < 1)
        {
            return false;
        }

        if (!TryTake(ref rest, OffsetKey)
            || !TryTakeToSpace(ref rest, out var offsetText)
            || !long.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset < 0)
        {
            return false;
        }

        if (!TryTake(ref rest, PathKey) || rest.IsEmpty || rest[0] != '$')
        {
            return false;
        }

        // The path closes the sentence, so a declared name holding a space stays part of it.
        path = rest.ToString();
        return true;
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
}
