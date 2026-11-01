using Microsoft.Extensions.Configuration;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// What is removed from telemetry before it leaves the process: the sensitive
/// values the deployment configures (<c>Telemetry:Redaction:Values</c>, empty by
/// default), the storage root path (<c>Storage:Filesystem:Root</c>, when it is
/// configured), any attribute whose name matches
/// <see cref="DeniedNamePattern"/> case-insensitively, and any value above
/// <see cref="TruncateAboveCharacters"/> characters, which keeps report content
/// — when reports exist — out of a log or a span.
/// </summary>
internal sealed class RedactionOptions
{
    /// <summary>The configuration section of the configured sensitive values.</summary>
    public const string SectionPath = "Telemetry:Redaction";

    /// <summary>The setting that lists the sensitive values.</summary>
    public const string ValuesSetting = SectionPath + ":Values";

    /// <summary>The setting that names the storage root.</summary>
    public const string StorageRootSetting = "Storage:Filesystem:Root";

    /// <summary>What a removed value is replaced with.</summary>
    public const string Replacement = "[redacted]";

    /// <summary>What a truncated value ends with.</summary>
    public const string TruncationSuffix = "[truncated]";

    /// <summary>A value longer than this many characters is truncated.</summary>
    public const int TruncateAboveCharacters = 4096;

    /// <summary>How much of a truncated value is kept.</summary>
    public const int KeptCharacters = 1024;

    /// <summary>The attribute names whose value is never written, matched case-insensitively as substrings.</summary>
    public const string DeniedNamePattern = "password|secret|token|credential|connectionstring|apikey|accesskey";

    private static readonly string[] DeniedNames = [.. DeniedNamePattern.Split('|')];

    /// <summary>The configured sensitive values.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>The storage root path, when the process configures one.</summary>
    public string? StorageRoot { get; init; }

    /// <summary>Reads the options from a process's configuration.</summary>
    public static RedactionOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var values = configuration.GetSection(ValuesSetting).Get<string[]>() ?? [];
        var root = configuration[StorageRootSetting];
        return new RedactionOptions
        {
            Values = [.. values.Where(value => !string.IsNullOrWhiteSpace(value))],
            StorageRoot = string.IsNullOrWhiteSpace(root) ? null : root,
        };
    }

    /// <summary>Whether an attribute with this name is never written.</summary>
    public static bool IsDeniedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return DeniedNames.Any(denied => name.Contains(denied, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Replaces every configured sensitive value and the storage root path with
    /// <see cref="Replacement"/>, then truncates a value above
    /// <see cref="TruncateAboveCharacters"/> characters.
    /// </summary>
    public string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = value;
        foreach (var sensitive in Values)
        {
            redacted = redacted.Replace(sensitive, Replacement, StringComparison.Ordinal);
        }

        if (StorageRoot is { } root)
        {
            redacted = redacted.Replace(root, Replacement, StringComparison.Ordinal);
        }

        return redacted.Length > TruncateAboveCharacters
            ? string.Concat(redacted.AsSpan(0, KeptCharacters), TruncationSuffix)
            : redacted;
    }
}
