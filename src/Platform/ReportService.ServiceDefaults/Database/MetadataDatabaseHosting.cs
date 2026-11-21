using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ReportService.ServiceDefaults;

/// <summary>
/// Adds the metadata database to a process that uses it: its two settings with
/// their start-up validation, the journal reader, and the readiness check named
/// <c>metadata-database</c>. The check carries no tag, so it is part of
/// readiness and not a diagnostic.
/// </summary>
public static class MetadataDatabaseHosting
{
    /// <summary>Binds the settings, registers the reader and adds the readiness check.</summary>
    public static IHostApplicationBuilder AddMetadataDatabase(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceConfiguration();
        builder.Services.AddOptions<DatabaseOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ConnectionString = configuration[DatabaseOptions.ConnectionStringSetting] ?? string.Empty;
            options.ExpectedSchemaVersion = configuration[DatabaseOptions.ExpectedSchemaVersionSetting] ?? string.Empty;
        });
        builder.Services.AddSingleton(new SettingsValidation(services =>
            [.. services.GetRequiredService<IOptions<DatabaseOptions>>().Value.Validate()]));
        builder.Services.AddSingleton<ISchemaJournalReader>(services =>
            new NpgsqlSchemaJournalReader(services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        // The singleton keeps the last state of the check, so a steady state logs once.
        builder.Services.AddSingleton<DatabaseHealthCheck>();
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name);
        return builder;
    }
}
