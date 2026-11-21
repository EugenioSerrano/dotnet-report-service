using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReportService.ServiceDefaults;

/// <summary>
/// Runs every registered settings check before any hosted service starts (the
/// web server included), so a process with a faulty configuration never
/// serves a request or reports ready. All problems are reported together.
/// </summary>
public sealed partial class ConfigurationValidation(
    IServiceProvider services,
    IHostEnvironment environment,
    ILogger<ConfigurationValidation> logger) : IHostedLifecycleService
{
    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        LogStarting(environment.ApplicationName, ProductVersion());
        var problems = services.GetServices<SettingsValidation>().SelectMany(v => v.Check(services)).ToList();
        if (problems.Count > 0)
        {
            throw new ConfigurationError(problems);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The product version of the running process, from its informational version.</summary>
    public static string ProductVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    [LoggerMessage(Level = LogLevel.Information, Message = "{Application} {Version} starting; validating its configuration")]
    private partial void LogStarting(string application, string version);
}
