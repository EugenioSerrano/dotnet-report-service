using Microsoft.Extensions.Hosting;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>
/// The one priority class a render or merge worker process serves: capacity per class is
/// dedicated, so each process polls exactly one class's queue. The setting is optional and,
/// absent, means the normal class - the queue every deployment without priorities already
/// serves. A value that names no class, or more than one, stops the process at start naming
/// the setting.
/// </summary>
public static class ServedClassOptions
{
    /// <summary>The setting's path.</summary>
    public const string SettingPath = "Priority:ServedClass";

    /// <summary>The reason a refused value is given, never repeating the value.</summary>
    public const string Reason = "must name exactly one class: high, normal or low";

    /// <summary>The setting's value in <paramref name="builder"/>'s configuration, or none.</summary>
    /// <param name="builder">The process being built.</param>
    public static string? Read(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Configuration[SettingPath];
    }

    /// <summary>The problems the setting's value has: none when <paramref name="accepts"/> accepts it.</summary>
    /// <param name="value">The setting's value.</param>
    /// <param name="accepts">Whether a value names exactly one class.</param>
    public static IReadOnlyList<ConfigurationProblem> Problems(string? value, Func<string?, bool> accepts)
    {
        ArgumentNullException.ThrowIfNull(accepts);
        return accepts(value) ? [] : [new ConfigurationProblem(SettingPath, Reason)];
    }

    /// <summary>Stops the process at start when the setting names no class.</summary>
    /// <param name="builder">The process being built.</param>
    /// <param name="accepts">Whether a value names exactly one class.</param>
    public static IHostApplicationBuilder AddServedClassCheck(this IHostApplicationBuilder builder, Func<string?, bool> accepts)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(accepts);
        return builder.AddSettingsCheck(_ => Problems(builder.Configuration[SettingPath], accepts));
    }
}
