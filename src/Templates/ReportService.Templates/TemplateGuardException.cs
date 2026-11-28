namespace ReportService.Templates;

/// <summary>The error codes a refusal of this assembly carries.</summary>
public static class TemplateErrorCodes
{
    /// <summary>The composed text passed a guard value.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>The composed text was not produced within its deadline.</summary>
    public const string RenderTimeout = "RENDER_TIMEOUT";

    /// <summary>The source of an image in the composed text is not an image data URI the output admits.</summary>
    public const string ImageSourceInvalid = "IMAGE_SOURCE_INVALID";

    /// <summary>The package declares an engine or a capability this build does not compose.</summary>
    public const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";
}

/// <summary>
/// A render that a guard stopped: the composed HTML passed its size, or the
/// deadline was reached. Both are deterministic refusals of the composition, not
/// of the printing.
/// </summary>
/// <param name="Code">The error code the refusal answers with.</param>
/// <param name="Reason">Why the render was stopped, in words that carry no data value.</param>
public sealed class TemplateGuardException(string code, string reason) : Exception(reason)
{
    /// <summary>The error code the refusal answers with.</summary>
    public string Code { get; } = code;

    /// <summary>The declared limit the render passed, when one applies.</summary>
    public string? Limit { get; init; }

    /// <summary>The refusal with a declared limit.</summary>
    public static TemplateGuardException Quota(string limit, string reason) =>
        new(TemplateErrorCodes.QuotaExceeded, reason) { Limit = limit };

    /// <summary>The part that was being composed when an image source was refused, when one was.</summary>
    public TemplatePart? Part { get; init; }

    /// <summary>The record whose row was being composed, or 0 outside a row part.</summary>
    public long Ordinal { get; init; }

    /// <summary>The attribute that carried the refused image source, lower-cased.</summary>
    public string? Attribute { get; init; }

    /// <summary>
    /// The refusal for an image source that is not an image data URI the output admits. Its
    /// reason names the part, the record ordinal and the attribute, in that order, and never
    /// the value.
    /// </summary>
    /// <param name="part">The part being composed.</param>
    /// <param name="ordinal">The record of a row part, or 0.</param>
    /// <param name="attribute">The attribute, lower-cased.</param>
    public static TemplateGuardException ImageSource(TemplatePart part, long ordinal, string attribute) =>
        new(TemplateErrorCodes.ImageSourceInvalid, ImageSourceReason(part, ordinal, attribute))
        {
            Part = part,
            Ordinal = ordinal,
            Attribute = attribute,
        };

    /// <summary>The stored reason of an image-source refusal: <c>part=&lt;part&gt; ordinal=&lt;n&gt; attribute=&lt;name&gt;</c>.</summary>
    /// <param name="part">The part being composed.</param>
    /// <param name="ordinal">The record of a row part, or 0.</param>
    /// <param name="attribute">The attribute, lower-cased.</param>
    public static string ImageSourceReason(TemplatePart part, long ordinal, string attribute) =>
        "part=" + PartToken(part)
        + " ordinal=" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " attribute=" + attribute;

    /// <summary>The stable token of a part in a reason.</summary>
    /// <param name="part">The part.</param>
    public static string PartToken(TemplatePart part) => part switch
    {
        TemplatePart.DocumentStart => "document-start",
        TemplatePart.GroupStart => "group-start",
        TemplatePart.Row => "row",
        TemplatePart.GroupEnd => "group-end",
        TemplatePart.DocumentEnd => "document-end",
        _ => "part",
    };

    /// <summary>The refusal for a render that passed its deadline.</summary>
    public static TemplateGuardException Timeout(string reason) => new(TemplateErrorCodes.RenderTimeout, reason);
}
