using Microsoft.Extensions.Configuration;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The development-only telemetry diagnostics: the metric capture, its read
/// endpoint and the two endpoints that prove context propagation and job
/// correlation. They are mounted and registered only when
/// <c>Telemetry:Diagnostics:Enabled</c> is true — the development and test
/// compositions set it, a deployed profile does not — and the capture is not a
/// telemetry destination: it records the points the pipeline was handed,
/// whether or not any destination is configured.
/// </summary>
internal sealed class TelemetryDiagnosticsSettings
{
    /// <summary>The configuration section of the diagnostics.</summary>
    public const string SectionPath = "Telemetry:Diagnostics";

    /// <summary>The switch that mounts and registers them.</summary>
    public const string EnabledSetting = SectionPath + ":Enabled";

    /// <summary>The capture reader's export interval, in milliseconds.</summary>
    public const string ExportIntervalSetting = SectionPath + ":ExportIntervalMilliseconds";

    /// <summary>The interval the metric pipeline uses when the composition sets none.</summary>
    public const int DefaultExportIntervalMilliseconds = 60_000;

    /// <summary>The shortest interval the capture accepts.</summary>
    public const int MinimumExportIntervalMilliseconds = 1_000;

    /// <summary>Whether the diagnostics are on.</summary>
    public bool Enabled { get; private init; }

    /// <summary>The capture reader's export interval, in milliseconds.</summary>
    public int ExportIntervalMilliseconds { get; private init; } = DefaultExportIntervalMilliseconds;

    /// <summary>Reads the settings from a process's configuration.</summary>
    public static TelemetryDiagnosticsSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var interval = configuration.GetValue<int?>(ExportIntervalSetting) ?? DefaultExportIntervalMilliseconds;
        return new TelemetryDiagnosticsSettings
        {
            Enabled = configuration.GetValue<bool>(EnabledSetting),
            ExportIntervalMilliseconds = Math.Max(interval, MinimumExportIntervalMilliseconds),
        };
    }
}
