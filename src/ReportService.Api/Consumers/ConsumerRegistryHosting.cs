using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Consumers;

/// <summary>How the API loads, watches and exposes its consumer registry.</summary>
public static class ConsumerRegistryHosting
{
    /// <summary>The optional setting naming the registry file; without it, <c>consumers.json</c> in the content root.</summary>
    public const string FileSetting = "Consumers:File";

    /// <summary>The tag of health checks that are diagnostics, not readiness.</summary>
    public const string DiagnosticTag = "diagnostic";

    /// <summary>The read-only diagnostic endpoint of the registry.</summary>
    public const string DiagnosticPath = "/health/registry";

    private const string DefaultFileName = "consumers.json";

    /// <summary>
    /// Adds the registry file to the configuration, watched for changes, and
    /// registers the registry and its diagnostic check. A missing file stops
    /// start-up naming <see cref="FileSetting"/> (never the path, a value).
    /// </summary>
    public static IHostApplicationBuilder AddConsumerRegistry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configured = builder.Configuration[FileSetting];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(builder.Environment.ContentRootPath, DefaultFileName)
            : Path.GetFullPath(configured, builder.Environment.ContentRootPath);
        if (!File.Exists(path))
        {
            throw new ConfigurationError(FileSetting, "the registry file does not exist");
        }

        // The registry file has a configuration of its own: its "consumers" list cannot collide with
        // the "Consumers:File" setting (configuration keys ignore case), and a change to any other
        // settings file never re-reads the registry.
        builder.Services.AddKeyedSingleton<IConfigurationRoot>(RegistryConfigurationKey, (_, _) => BuildRegistryConfiguration(path));
        builder.Services.AddSingleton(services => new ConsumerRegistry(
            services.GetRequiredKeyedService<IConfigurationRoot>(RegistryConfigurationKey),
            services.GetRequiredService<ILogger<ConsumerRegistry>>()));
        builder.Services.AddHealthChecks().AddCheck<ConsumerRegistryHealthCheck>(ConsumerRegistryHealthCheck.Name, tags: [DiagnosticTag]);
        return builder;
    }

    /// <summary>
    /// The registry file as a watched configuration. A file that cannot be parsed
    /// leaves the configuration empty instead of throwing; the registry then
    /// refuses it and keeps the last valid one (or stops start-up).
    /// </summary>
    public static IConfigurationRoot BuildRegistryConfiguration(string path) =>
        new ConfigurationBuilder()
            .AddJsonFile(source =>
            {
                source.Path = path;
                source.ResolveFileProvider();
                source.Optional = false;
                source.ReloadOnChange = true;
                source.OnLoadException = context => context.Ignore = true;
            })
            .Build();

    private const string RegistryConfigurationKey = "consumer-registry";

    /// <summary>Validates the registry before the service starts: an invalid registry stops start-up here.</summary>
    public static WebApplication UseConsumerRegistry(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Services.GetRequiredService<ConsumerRegistry>();
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = r => !r.Tags.Contains(DiagnosticTag),
            ResponseWriter = HealthResponseWriter.Json,
        });
        app.MapHealthChecks(DiagnosticPath, new HealthCheckOptions
        {
            Predicate = r => r.Name == ConsumerRegistryHealthCheck.Name,
            ResponseWriter = HealthResponseWriter.Json,
        });
        return app;
    }
}
