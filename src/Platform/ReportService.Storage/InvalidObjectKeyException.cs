namespace ReportService.Storage;

/// <summary>
/// Raised when a value is not a valid object key. The message names the rule
/// the value breaks and never repeats the value.
/// </summary>
public sealed class InvalidObjectKeyException : ArgumentException
{
    /// <summary>Creates the exception for the broken rule.</summary>
    public InvalidObjectKeyException(string reason)
        : base($"The object key is not valid: {reason}.")
    {
        Reason = reason;
    }

    /// <summary>The rule the value breaks.</summary>
    public string Reason { get; }
}
