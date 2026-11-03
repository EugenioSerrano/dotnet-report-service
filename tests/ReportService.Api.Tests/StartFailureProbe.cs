using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Api.Consumers;
using ReportService.Api.Uploads;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Api.Tests;

/// <summary>
/// The host one case builds for itself and starts to read the refusal a misconfigured
/// process gives: the product's own start-up registration over a plain host, with the
/// case's configuration, its service replacements and the logging it collects. Two
/// probes in one process are two independent hosts, so a case's verdict never depends on
/// a host another factory built; no web host and no server are composed, so nothing binds
/// a port and nothing is served.
/// </summary>
internal sealed class StartFailureProbe : IDisposable
{
    /// <summary>The registry the API project's content root carries, written into a directory of the probe's own.</summary>
    private const string Registry =
        """{ "consumers": [ { "consumerId": "dev-consumer", "active": true }, { "consumerId": "other-consumer", "active": true } ] }""";

    private readonly TemporaryDirectory _directory;
    private readonly IHost _host;

    private StartFailureProbe(IHost host, TemporaryDirectory directory)
    {
        _host = host;
        _directory = directory;
    }

    /// <summary>
    /// Composes and builds the host of <paramref name="settings"/>, the case's own
    /// configuration values, with <paramref name="replacements"/> applied to the built
    /// container and <paramref name="logProviders"/> added to the host's logging. The
    /// content root is the API project's directory, the one the process itself reads its
    /// settings files from, so the case keeps the configuration it had before.
    /// </summary>
    public static StartFailureProbe Create(
        IReadOnlyDictionary<string, string?> settings,
        string environmentName = "Development",
        Action<IServiceCollection>? replacements = null,
        params ILoggerProvider[] logProviders)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // The process reads its registry from the content root; the probe keeps that input
        // in a directory of its own unless the case supplies a registry file itself.
        var directory = new TemporaryDirectory();
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!settings.ContainsKey(ConsumerRegistryHosting.FileSetting))
        {
            configuration[ConsumerRegistryHosting.FileSetting] = directory.Write("consumers.json", Registry);
        }

        foreach (var (key, value) in settings)
        {
            configuration[key] = value;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName,
            ContentRootPath = Path.Combine(RepositoryPaths.Root, "src", "ReportService.Api"),
        });
        builder.Configuration.AddInMemoryCollection(configuration);

        // The product's start-up registration, in the order the process performs it; the
        // telemetry registration is left out, because it carries no start-up validation.
        builder.AddMetadataDatabase();
        builder.AddConsumerRegistry();
        builder.AddObjectStorage("api");
        builder.AddValidatedConfiguration<UploadOptions>(UploadOptions.SectionPath);

        replacements?.Invoke(builder.Services);
        foreach (var provider in logProviders)
        {
            builder.Logging.AddProvider(provider);
        }

        return new StartFailureProbe(builder.Build(), directory);
    }

    /// <summary>
    /// Starts the host and returns the configuration error it refused to start with. A start
    /// that raises no exception reports that fact as a failure of the case, never as a pass.
    /// </summary>
    public ConfigurationError Refusal() => ConfigurationErrors.Find(Record.Exception(() => _host.Start()));

    public void Dispose()
    {
        _host.Dispose();
        _directory.Dispose();
    }
}
