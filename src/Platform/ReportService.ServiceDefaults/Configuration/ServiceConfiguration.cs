using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ReportService.ServiceDefaults;

/// <summary>
/// Validated configuration for every process: settings records are bound from
/// their configuration section and checked before the process starts serving
/// or reports ready.
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    /// Enables the start-up validation of every settings record registered with
    /// <see cref="AddValidatedConfiguration{T}"/>. Calling it more than once has
    /// no further effect.
    /// </summary>
    public static IHostApplicationBuilder AddServiceConfiguration(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConfigurationValidation>());
        return builder;
    }

    /// <summary>
    /// Binds <typeparamref name="T"/> from <paramref name="sectionPath"/> and
    /// validates it at start: a faulty setting stops the host with a
    /// <see cref="ConfigurationError"/> before it serves traffic.
    /// </summary>
    public static IHostApplicationBuilder AddValidatedConfiguration<T>(this IHostApplicationBuilder builder, string sectionPath)
        where T : class, IValidatedSettings, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        builder.AddServiceConfiguration();
        builder.Services.AddOptions<T>().Bind(builder.Configuration.GetSection(sectionPath));
        builder.Services.AddSingleton(new SettingsValidation(
            services => Problems(services.GetRequiredService<IOptions<T>>().Value, sectionPath)));
        return builder;
    }

    /// <summary>Binds <typeparamref name="T"/> from a configuration section; missing settings keep their defaults.</summary>
    public static T Bind<T>(IConfiguration configuration, string sectionPath)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(sectionPath).Get<T>() ?? new T();
    }

    /// <summary>
    /// Registers a start-up check of its own, for a rule that spans more than one
    /// settings record: one record cannot see another's values, so a rule such as
    /// "this bound has to be at least as long as that one" is checked here. The
    /// check returns the problems it finds, each naming its setting fully and never
    /// a value; any of them stops the process like a record's own problem does.
    /// </summary>
    /// <param name="builder">The process being built.</param>
    /// <param name="check">The rule to apply at start.</param>
    public static IHostApplicationBuilder AddSettingsCheck(
        this IHostApplicationBuilder builder,
        Func<IServiceProvider, IEnumerable<ConfigurationProblem>> check)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(check);
        builder.AddServiceConfiguration();
        builder.Services.AddSingleton(new SettingsValidation(services => [.. check(services)]));
        return builder;
    }

    /// <summary>Throws a <see cref="ConfigurationError"/> naming every faulty setting of <paramref name="settings"/>.</summary>
    public static void Validate<T>(T settings, string sectionPath)
        where T : IValidatedSettings
    {
        var problems = Problems(settings, sectionPath);
        if (problems.Count > 0)
        {
            throw new ConfigurationError(problems);
        }
    }

    private static List<ConfigurationProblem> Problems<T>(T settings, string sectionPath)
        where T : IValidatedSettings =>
        [.. settings.Validate().Select(p => p with { Setting = $"{sectionPath}:{p.Setting}" })];
}

/// <summary>One registered settings check, run at start.</summary>
internal sealed record SettingsValidation(Func<IServiceProvider, IReadOnlyList<ConfigurationProblem>> Check);
