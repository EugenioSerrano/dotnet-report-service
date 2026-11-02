namespace ReportService.Api.Http;

/// <summary>
/// The stable codes an error answer carries in its <c>code</c> member. A caller
/// branches on the code, never on the prose of a message; the same code always
/// means the same condition, whatever the language of the detail.
/// </summary>
public static class ErrorCodes
{
    /// <summary>The request declares no usable consumer identity.</summary>
    public const string ConsumerUnknown = "CONSUMER_UNKNOWN";

    /// <summary>The declared consumer is in the registry and is not active; it may not open a new upload.</summary>
    public const string ConsumerInactive = "CONSUMER_INACTIVE";

    /// <summary>The request asks for something this increment does not offer.</summary>
    public const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";

    /// <summary>The request carries more than the operation accepts.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>The request is not a valid document of the operation.</summary>
    public const string RequestInvalid = "REQUEST_INVALID";

    /// <summary>The named upload does not exist for the declared consumer.</summary>
    public const string InputNotFound = "INPUT_NOT_FOUND";

    /// <summary>The named template version does not exist for the declared consumer.</summary>
    public const string TemplateNotFound = "TEMPLATE_NOT_FOUND";

    /// <summary>The upload does not admit the operation in its current state.</summary>
    public const string UploadStateConflict = "UPLOAD_STATE_CONFLICT";

    /// <summary>The service could not complete the request for a cause on its side.</summary>
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
}
