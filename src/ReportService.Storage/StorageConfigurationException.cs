namespace ReportService.Storage;

/// <summary>
/// Raised when a store cannot start: names the faulty setting and the reason,
/// in the form <c>setting '&lt;path&gt;': &lt;reason&gt;</c>, and never the
/// setting's value.
/// </summary>
public sealed class StorageConfigurationException : Exception
{
    /// <summary>Creates the exception for one setting.</summary>
    public StorageConfigurationException(string setting, string reason, Exception? inner = null)
        : base($"setting '{setting}': {reason}", inner)
    {
        Setting = setting;
        Reason = reason;
    }

    /// <summary>The setting path, for example <c>Storage:Filesystem:Root</c>.</summary>
    public string Setting { get; }

    /// <summary>Why the setting is refused, in words that never repeat its value.</summary>
    public string Reason { get; }
}
