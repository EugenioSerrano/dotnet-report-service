namespace ReportService.Templates;

/// <summary>What a template's presentation refusal becomes: a stable code and reason, the function and the record.</summary>
/// <param name="Code">The error code the block ends with.</param>
/// <param name="Reason">The stable reason within that code.</param>
/// <param name="Function">The presentation function that refused.</param>
/// <param name="RecordOrdinal">The record the refusal belongs to, when it belongs to one.</param>
public sealed record PresentationFailure(string Code, string Reason, string Function, long? RecordOrdinal)
{
    /// <summary>
    /// The one-line diagnostic: a code, a reason, a function and a record - never a value
    /// of the data and never the format's expansion.
    /// </summary>
    public string Message => RecordOrdinal is { } ordinal
        ? $"{Code} reason={Reason} function={Function} record={ordinal}"
        : $"{Code} reason={Reason} function={Function}";
}

/// <summary>
/// The one place a presentation refusal becomes an error code and a reason. The codes are
/// the service's own catalogue; a refusal is never retried, because the same value and the
/// same format reach the same answer.
/// </summary>
public static class PresentationFailures
{
    /// <summary>The code a value that cannot be presented ends with.</summary>
    public const string DataInvalid = "DATA_INVALID";

    /// <summary>The code a format the runtime rejects ends with.</summary>
    public const string TemplateInvalid = "TEMPLATE_INVALID";

    /// <summary>The reason a number has no exact decimal.</summary>
    public const string NumberOutOfRange = "number-out-of-range";

    /// <summary>The reason a value is not the type the function needs, or is missing.</summary>
    public const string TypeMismatch = "type-mismatch";

    /// <summary>The reason a format is outside its family or rejected by the runtime.</summary>
    public const string FormatString = "format-string";

    /// <summary>Describes <paramref name="refusal"/> for the block's failure.</summary>
    /// <param name="refusal">Why the function refused.</param>
    /// <param name="function">The function's name.</param>
    /// <param name="recordOrdinal">The record the refusal belongs to, or <see langword="null"/>.</param>
    public static PresentationFailure Describe(PresentationRefusal refusal, string function, long? recordOrdinal)
    {
        ArgumentNullException.ThrowIfNull(function);
        return refusal switch
        {
            PresentationRefusal.NoDecimal => new PresentationFailure(DataInvalid, NumberOutOfRange, function, recordOrdinal),
            PresentationRefusal.NotADate => new PresentationFailure(DataInvalid, TypeMismatch, function, recordOrdinal),
            _ => new PresentationFailure(TemplateInvalid, FormatString, function, recordOrdinal),
        };
    }
}
