namespace ReportService.Api.Http;

/// <summary>
/// The contract every malformed-request answer follows, defined once for the
/// whole API: one problem per answer, chosen by a fixed precedence so the same
/// request always gets the same answer; a <c>reason</c> constant and the known
/// field path; and never a value the caller sent, its length or its type name.
/// A member the reader does not know is counted, never named, because its name is
/// caller text like any other.
/// </summary>
public static class RequestInvalidDetails
{
    /// <summary>The title of every malformed-request answer.</summary>
    public const string Title = "Invalid request";

    /// <summary>The body was not sent as JSON.</summary>
    public const string MediaTypeReason = "media-type";

    /// <summary>The body is not one JSON object, or carries more after it.</summary>
    public const string MalformedJsonReason = "malformed-json";

    /// <summary>The body carries members the operation does not know.</summary>
    public const string UnknownFieldReason = "unknown-field";

    /// <summary>A known member appears more than once.</summary>
    public const string DuplicateFieldReason = "duplicate-field";

    /// <summary>A required member is absent.</summary>
    public const string MissingFieldReason = "missing-field";

    /// <summary>A required member is present and null.</summary>
    public const string NullFieldReason = "null-field";

    /// <summary>A member holds a value of the wrong JSON type.</summary>
    public const string WrongTypeReason = "wrong-type";

    /// <summary>A member holds a string that breaks the rule its reader declares.</summary>
    public const string InvalidFormatReason = "invalid-format";
}

/// <summary>
/// One malformed-request condition: the reason it was chosen, the known field
/// path it concerns, the count of unknown members, and the constant detail its
/// answer carries. Instances are built only by the factories below, so the texts
/// and the precedence cannot drift from one reader to the next.
/// </summary>
public sealed class InvalidRequestBody
{
    private InvalidRequestBody(string reason, string? field, string detail, int unknownFieldCount = 0)
    {
        Reason = reason;
        Field = field;
        Detail = detail;
        UnknownFieldCount = unknownFieldCount;
    }

    /// <summary>One of the reason constants of <see cref="RequestInvalidDetails"/>.</summary>
    public string Reason { get; }

    /// <summary>The known field path, or <see langword="null"/> when the condition concerns no single member.</summary>
    public string? Field { get; }

    /// <summary>The constant explanation, with the known path substituted when there is one.</summary>
    public string Detail { get; }

    /// <summary>The number of unknown members, when <see cref="Reason"/> is <c>unknown-field</c>; zero otherwise.</summary>
    public int UnknownFieldCount { get; }

    /// <summary>The two extension members every malformed answer carries, plus the count for an unknown member.</summary>
    public IReadOnlyDictionary<string, object?> Extensions
    {
        get
        {
            var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["field"] = Field,
                ["reason"] = Reason,
            };
            if (string.Equals(Reason, RequestInvalidDetails.UnknownFieldReason, StringComparison.Ordinal))
            {
                extensions["unknownFieldCount"] = UnknownFieldCount;
            }

            return extensions;
        }
    }

    /// <summary>The body was not sent as <c>application/json</c>.</summary>
    public static InvalidRequestBody MediaType() => new(
        RequestInvalidDetails.MediaTypeReason,
        null,
        "The request body must be sent as application/json.");

    /// <summary>The body is not one JSON object with nothing after it.</summary>
    public static InvalidRequestBody MalformedJson() => new(
        RequestInvalidDetails.MalformedJsonReason,
        null,
        "The request body must be a single JSON object.");

    /// <summary>The body carries <paramref name="count"/> members the operation does not know.</summary>
    /// <param name="count">How many unknown members the body carries.</param>
    public static InvalidRequestBody UnknownField(int count) => new(
        RequestInvalidDetails.UnknownFieldReason,
        null,
        count == 1
            ? "The request contains 1 field that is not part of this operation."
            : $"The request contains {count} fields that are not part of this operation.",
        count);

    /// <summary><paramref name="field"/> appears more than once.</summary>
    /// <param name="field">The known field path.</param>
    public static InvalidRequestBody DuplicateField(string field) => new(
        RequestInvalidDetails.DuplicateFieldReason,
        field,
        $"The field '{field}' appears more than once.");

    /// <summary><paramref name="field"/> is required and absent.</summary>
    /// <param name="field">The known field path.</param>
    public static InvalidRequestBody MissingField(string field) => new(
        RequestInvalidDetails.MissingFieldReason,
        field,
        $"The field '{field}' is required.");

    /// <summary><paramref name="field"/> is present and null.</summary>
    /// <param name="field">The known field path.</param>
    public static InvalidRequestBody NullField(string field) => new(
        RequestInvalidDetails.NullFieldReason,
        field,
        $"The field '{field}' must not be null.");

    /// <summary><paramref name="field"/> holds a value of another JSON type.</summary>
    /// <param name="field">The known field path.</param>
    /// <param name="expected">The JSON type the reader expects: <c>string</c>, <c>array</c> or <c>object</c>.</param>
    public static InvalidRequestBody WrongType(string field, string expected) => new(
        RequestInvalidDetails.WrongTypeReason,
        field,
        $"The field '{field}' must be a {expected}.");

    /// <summary><paramref name="field"/> holds a string that breaks its reader's rule.</summary>
    /// <param name="field">The known field path.</param>
    /// <param name="detail">The constant sentence the reader declares for the rule, stating it and never the value.</param>
    public static InvalidRequestBody InvalidFormat(string field, string detail) => new(
        RequestInvalidDetails.InvalidFormatReason,
        field,
        detail);
}
